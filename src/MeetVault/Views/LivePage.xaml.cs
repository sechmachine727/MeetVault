using System.Collections.ObjectModel;
using MeetVault.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    private enum LiveState { Idle, Recording, Paused }

    public ObservableCollection<TranscriptLine> Lines { get; } = new();

    private MeetingSession? _session;
    private WhisperTranscriber? _whisper;
    private NAudioCapture? _capture;
    private readonly List<byte> _window = new();
    private const int WindowBytes = 16000 * 2 * 6; // 6 s of 16 kHz mono
    private WindowDiarizer? _windowDiarizer;
    private ISpeechTranscriber _transcriber = null!;
    private IAudioCapture _audio = null!;
    private ISpeakerDiarizer _diarizer = null!;
    private string _engineMode = "demo (no models)";
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");
    private long _windowOffsetMs;
    private readonly Dictionary<string, string> _speakerNames = new();
    private string _micLabel = "not started";
    private string _modelLabel = "tiny";
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private int _droppedWindows;
    private volatile bool _acceleratorReported;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _checkpoint;
    private LiveState _state = LiveState.Idle;

    // Microphone and system audio are recorded together unless switched off in Settings.
    private static string MicId => AppStore.Repo.GetSetting("audio.mic.enabled") == "off"
        ? "off"
        : AppStore.Repo.GetSetting("audio.mic") ?? "0";
    private static string AppSource
    {
        get
        {
            if (AppStore.Repo.GetSetting("audio.system.enabled") == "off") return "off";
            var value = AppStore.Repo.GetSetting("audio.source");
            return string.IsNullOrWhiteSpace(value) ? "all" : value;
        }
    }

    public LivePage()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Lines.ToArray();
        Lines.CollectionChanged += (_, _) => { TranscriptList.ItemsSource = Lines.ToArray(); TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Leaving Live pauses an active meeting; a draft stays recoverable on the Dashboard.
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _checkpoint?.Stop();
        LiveBridge.RenameByName = null;
        try { if (_state != LiveState.Idle && _session is not null) await _session.PauseAsync(); } catch { }
        try { await _transcribeGate.WaitAsync(); _transcribeGate.Release(); } catch { } // drain in-flight window
        try { _capture?.Dispose(); } catch { }
        try { _whisper?.Dispose(); } catch { }
        try { _windowDiarizer?.Dispose(); } catch { }
    }

    // Engines resolve at load; recording starts only on an explicit action or a resume.
    private void ResolveEngines()
    {
        ISpeechTranscriber transcriber = new FakeTranscriber();
        IAudioCapture audio = new FakeAudioCapture();
        ISpeakerDiarizer diarizer = new FakeDiarizer();
        _engineMode = "demo (add models to run live)";
        if (!EngineModels.Available) { _transcriber = transcriber; _audio = audio; _diarizer = diarizer; return; }

        WhisperTranscriber? whisper = null;
        NAudioCapture? capture = null;
        WindowDiarizer? windowDiarizer = null;
        try
        {
            var picked = AppStore.Repo.GetSetting("transcription.model") == "base" ? "ggml-base.bin" : "ggml-tiny.bin";
            var modelFile = File.Exists(Path.Combine(ModelDir, picked)) ? picked : EngineModels.WhisperModel!;
            var (backend, deviceIndex) = TranscriptionDevice.Parse(AppStore.Repo.GetSetting("transcription.device"));
            whisper = new WhisperTranscriber(Path.Combine(ModelDir, modelFile), "auto", backend, deviceIndex);
            capture = new NAudioCapture();
            if (AppStore.Repo.GetSetting("transcription.diarization") != "off")
            {
                var dz = new SherpaDiarizer(Path.Combine(ModelDir, "sherpa-seg.onnx"), Path.Combine(ModelDir, "sherpa-emb.onnx"));
                windowDiarizer = new WindowDiarizer(dz);
            }
            _whisper = whisper; _capture = capture; _windowDiarizer = windowDiarizer;
            _transcriber = whisper; _audio = capture; _diarizer = (ISpeakerDiarizer?)windowDiarizer ?? diarizer;
            capture.PcmReady += OnPcm;
            capture.CaptureFailed += OnCaptureFailed;
            _modelLabel = modelFile.Replace("ggml-", "").Replace(".bin", "");
            _engineMode = "live: whisper " + _modelLabel + (windowDiarizer is null ? ", single speaker" : " + sherpa diarizer");
            return;
        }
        catch
        {
            try { capture?.Dispose(); } catch { }
            try { whisper?.Dispose(); } catch { }
            try { windowDiarizer?.Dispose(); } catch { }
            _whisper = null; _capture = null; _windowDiarizer = null;
            _engineMode = "demo (engine init failed)";
            MeetVault.Core.AppLog.Error("live: engine init failed; running demo");
        }
        _transcriber = transcriber; _audio = audio; _diarizer = diarizer;
        MeetVault.Core.AppLog.Info("live: engines resolved -> " + _engineMode);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveEngines();
        UpdateEngineStatus(actual: false);
        _micLabel = NAudioCapture.ListDevices().FirstOrDefault(d => d.Id.ToString() == MicId).Name
            ?? (MicId == "0" ? "Windows default" : "device " + MicId);
        var id = PendingRecovery.MeetingId;
        PendingRecovery.MeetingId = null;
        if (id is not null) await RecoverAsync(id);
        else SetState(LiveState.Idle); // do not start on navigation; the user presses Start
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _session = new MeetingSession(_audio, _transcriber, _diarizer, new NeutralSpeakerLabeler(), AppStore.Repo);
            await _session.StartAsync(MicId, AppSource, CancellationToken.None);
            WireSession(clearOverlay: true);
            _windowOffsetMs = 0;
            _acceleratorReported = false;
            SetState(LiveState.Recording);
            UpdateEngineStatus(actual: true);
            RebuildSpeakers();
            Status(_engineMode.StartsWith("live") ? "Recording. Speaker turns appear below as they are recognized." : "Demo mode: add Whisper and diarizer models to models/ for live transcription.");
        }
        catch (Exception ex)
        {
            try { if (_session is not null) { _session.MarkFailed(); _session.Dispose(); _session = null; } } catch { }
            SetState(LiveState.Idle);
            MeetVault.Core.AppLog.Error("live: start capture failed", ex);
            Status("Could not start capture. Check the microphone in Settings. (" + Short(ex) + ")");
        }
    }

    private async Task RecoverAsync(string id)
    {
        try
        {
            if (AppStore.Repo.Load(id)?.MediaPath is { Length: > 0 } mediaPath && _capture is not null) _capture.ReuseFile(mediaPath);
            _session = await MeetingSession.RecoverAsync(id, _audio, _transcriber, _diarizer, new NeutralSpeakerLabeler(), AppStore.Repo, MicId, AppSource);
            foreach (var sp in _session.Speakers) _speakerNames[sp.Id] = sp.DisplayName;
            foreach (var s in _session.Segments) AddLine(s.SpeakerId, s.Text);
            WireSession(clearOverlay: false); // keep the recovered backlog for a late-opened overlay
            _windowOffsetMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            _acceleratorReported = false;
            SetState(_session.IsPaused ? LiveState.Paused : LiveState.Recording);
            UpdateEngineStatus(actual: true);
            RebuildSpeakers();
            Status($"Resumed '{id}'. {_session.Segments.Count} line(s) restored.");
        }
        catch (Exception ex)
        {
            SetState(LiveState.Idle);
            _capture?.ClearReuse(); // a recovery that failed must not capture the next meeting into the old file
            MeetVault.Core.AppLog.Error("live: resume failed", ex);
            Status("Could not resume this meeting. (" + Short(ex) + ")");
        }
    }

    private void WireSession(bool clearOverlay)
    {
        _session!.LiveSegment += OnLiveSegment;
        if (clearOverlay) LiveBridge.Clear();
        LiveBridge.RenameByName = RenameByDisplayName;
        StartCheckpointTimer();
    }

    // Single place that maps state to which actions are available.
    private void SetState(LiveState state)
    {
        _state = state;
        StartButton.Visibility = state == LiveState.Idle ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = state == LiveState.Idle;
        PauseButton.IsEnabled = state == LiveState.Recording;
        ResumeButton.IsEnabled = state == LiveState.Paused;
        StopButton.IsEnabled = state is LiveState.Recording or LiveState.Paused;
        RefreshAudioStatus();
        MeetVault.Core.AppLog.Info("live: state=" + state);
    }

    private void StartCheckpointTimer()
    {
        if (_checkpoint is not null) return; // one timer, one handler: never accumulate across start/recover
        _checkpoint = DispatcherQueue.CreateTimer();
        _checkpoint.Interval = TimeSpan.FromSeconds(15);
        _checkpoint.Tick += async (_, _) => { try { if (_session is not null) await _session.CheckpointAsync(); } catch { } };
        _checkpoint.Start();
    }

    private void RefreshAudioStatus()
        => AudioStatus.Text = _state == LiveState.Idle
            ? $"Mic: {_micLabel}. Not recording."
            : _state == LiveState.Paused ? $"Paused. Mic: {_micLabel}." : $"Recording from {_micLabel}.";

    // Shows the accelerator that actually loaded once recording has begun, so a GPU that
    // silently fell back to CPU is called out rather than shown as if it were used.
    private void UpdateEngineStatus(bool actual)
    {
        if (_whisper is null) { EngineStatus.Text = _engineMode; return; }
        var requested = _whisper.Backend;
        var loaded = _whisper.LoadedBackend;
        // The compute device the native actually initialized, not just which DLL loaded.
        var compute = _whisper.AcceleratorInUse;
        var accel = actual
            ? compute ?? (loaded == GpuBackend.Cpu ? "CPU" : loaded.ToString())
            : (requested == GpuBackend.Cpu ? "CPU" : requested.ToString());
        var note = actual && requested != GpuBackend.Cpu && accel == "CPU"
            ? " (requested " + requested + ", not usable here)"
            : actual && requested == GpuBackend.Cuda && !accel.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase)
                ? " (requested Cuda, running " + accel + ")"
                : actual && requested == GpuBackend.Vulkan && !accel.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase)
                    ? " (requested Vulkan, running " + accel + ")"
                    : "";
        EngineStatus.Text = "live: whisper " + _modelLabel + " + " + accel + note + (_windowDiarizer is null ? ", single speaker" : " + sherpa diarizer");
        if (actual) MeetVault.Core.AppLog.Info("live: accelerator=" + accel + " requested=" + requested + " native=" + loaded);
    }

    private void RebuildSpeakers()
    {
        SpeakerPanel.Children.Clear();
        if (_session is null) { SpeakerEmpty.Visibility = Visibility.Visible; return; }
        foreach (var sp in _session.Speakers)
        {
            _speakerNames[sp.Id] = sp.DisplayName;
            var id = sp.Id;
            var box = new TextBox { Text = sp.DisplayName, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "Speaker name" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Rename " + sp.DisplayName);
            void Commit() { Rename(id, box.Text); }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
            SpeakerPanel.Children.Add(box);
        }
        SpeakerEmpty.Visibility = _session.Speakers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Rename(string speakerId, string newName)
    {
        var trimmed = (newName ?? "").Trim();
        if (_session is null || trimmed.Length == 0 || !_speakerNames.TryGetValue(speakerId, out var old) || old == trimmed) return;
        _session.RenameSpeaker(speakerId, trimmed);
        _speakerNames[speakerId] = trimmed;
        for (var i = 0; i < Lines.Count; i++)
            if (Lines[i].Speaker == old) Lines[i].Speaker = trimmed;
        TranscriptList.ItemsSource = Lines.ToArray();
        LiveBridge.NotifyRenamed(old, trimmed); // keep an open overlay in sync
    }

    private async void OnPcm(byte[] chunk)
    {
        if (_whisper is null || !_whisper.IsRunning) return;
        lock (_window) _window.AddRange(chunk);
        byte[] window;
        lock (_window)
        {
            if (_window.Count < WindowBytes) return;
            window = _window.ToArray();
            _window.Clear();
        }
        var windowMs = window.Length / 32;
        var start = _windowOffsetMs;
        _windowOffsetMs = start + windowMs;
        // Leave the capture pump before the diarizer and whisper run: both are synchronous up
        // to their first await, and running them here would stall the thread that writes the
        // archive, which then drops source audio and loses wall-clock duration.
        await Task.Yield();
        if (!await _transcribeGate.WaitAsync(0))
        {
            // Transcription is behind real time and this window cannot wait. The recording
            // still holds the audio; the transcript loses it, so it must be visible in the log.
            var dropped = System.Threading.Interlocked.Increment(ref _droppedWindows);
            MeetVault.Core.AppLog.Warn("live: transcription behind real time, dropped " + (windowMs / 1000.0).ToString("0.0") + "s window (total " + dropped + ")");
            return;
        }
        try
        {
            if (_session?.MeetingId is not { Length: > 0 } meetingId) return;
            _windowDiarizer?.AssignWindowSamples(window);
            DispatcherQueue.TryEnqueue(() => PartialLine.Text = "Transcribing\u2026");
            await _whisper.TranscribeWindowAsync(window, start, meetingId);
            ReportAcceleratorOnce();
        }
        catch { }
        finally
        {
            _transcribeGate.Release();
            DispatcherQueue.TryEnqueue(() => PartialLine.Text = _state != LiveState.Idle ? "Listening\u2026" : "");
        }
    }

    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_speakerNames.ContainsKey(segment.SpeakerId)) RebuildSpeakers();
            AddLine(segment.SpeakerId, segment.Text);
        });

    private void AddLine(string speakerId, string text)
    {
        var name = _speakerNames.TryGetValue(speakerId, out var n) ? n : speakerId;
        Lines.Add(new TranscriptLine(name, text));
        LiveBridge.Publish(name, text);
    }

    private bool RenameByDisplayName(string oldName, string newName)
    {
        var id = _speakerNames.FirstOrDefault(kv => kv.Value == oldName).Key;
        if (id is null) return false;
        Rename(id, newName);
        return true;
    }

    private void Status(string message) => Status(InfoBarSeverity.Informational, message);

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // A requested GPU that did not load runs the whole meeting on the CPU. The engine line
    // already records it, but that is a quiet note; raise it as a warning so the downgrade is
    // never silent. Fires only once recording has begun, when the loaded backend is known.
    private void WarnIfGpuFallback()
    {
        if (_whisper is null) return;
        var requested = _whisper.Backend;
        if (requested == GpuBackend.Cpu) return;
        var compute = _whisper.AcceleratorInUse;
        var loaded = _whisper.LoadedBackend;
        // A missing reading is not evidence of a fallback: the native log subscription is
        // best-effort, so an absent report is logged and left to the engine line rather than
        // shown as a downgrade.
        if (compute is null && loaded != GpuBackend.Cpu)
        {
            MeetVault.Core.AppLog.Warn("live: whisper did not report a compute device; requested=" + requested + " native=" + loaded);
            return;
        }
        // Called after the first window, so the compute device is known. Ways a GPU request is not
        // honoured: the runtime did not load (loaded is CPU), whisper found no usable GPU (its own
        // "no GPU found" -> compute CPU), or a different backend's native is loaded (switching
        // without restart keeps the first native).
        string? problem = compute switch
        {
            null => "the " + requested + " runtime did not load",
            "CPU" => "the requested " + requested + " GPU is not usable here",
            _ when requested == GpuBackend.Cuda && !compute.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase)
                => "the loaded native is " + compute + ", so CUDA is ignored until the app is restarted",
            _ when requested == GpuBackend.Vulkan && !compute.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase)
                => "the loaded native is " + compute + ", so Vulkan is ignored until the app is restarted",
            _ => null,
        };
        if (problem is null) return;
        MeetVault.Core.AppLog.Warn("live: GPU not used as requested; requested=" + requested + " compute=" + (compute ?? "unknown") + " native=" + loaded);
        Status(InfoBarSeverity.Warning,
            "Transcription is not on the requested GPU: " + problem + ". Recording continues. "
            + "Check the graphics device in Settings, restart the app after changing it, or see the log.");
    }

    // whisper reports the compute device on its first window, not at start, so the engine line
    // and the fallback warning are refreshed once when that first report arrives.
    private void ReportAcceleratorOnce()
    {
        if (_acceleratorReported || _whisper is null) return;
        _acceleratorReported = true;
        DispatcherQueue.TryEnqueue(() => { UpdateEngineStatus(actual: true); WarnIfGpuFallback(); });
    }

    // Capture died on its own. The meeting is still open so the user can save what was
    // recorded, but they must not be left believing the microphone is still live.
    private void OnCaptureFailed(string reason)
        => DispatcherQueue.TryEnqueue(() => Status("Capture stopped unexpectedly (" + reason + "). Press Stop and save to keep what was recorded."));

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state != LiveState.Recording || _session is null) return;
        lock (_window) _window.Clear();
        try { await _session.PauseAsync(); SetState(LiveState.Paused); Status("Paused. Audio in the gap is not recorded and will not reappear on resume."); }
        catch (Exception ex) { Status("Could not pause: " + Short(ex)); }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_state != LiveState.Paused || _session is null) return;
        try
        {
            await _session.ResumeAsync();
            _windowOffsetMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            SetState(LiveState.Recording);
            Status("Resumed. Capture continues after the gap.");
        }
        catch (Exception ex) { Status("Could not resume: " + Short(ex)); }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_state == LiveState.Idle || _session is null) return;
        try
        {
            var confirm = new ContentDialog
            {
                Title = "Stop and save this meeting?",
                Content = "Recording stops and the meeting is saved with " + _session.Segments.Count + " line(s). You cannot add more to it afterwards.",
                PrimaryButtonText = "Stop and save",
                CloseButtonText = "Keep recording",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            await _session.StopAsync();
            var count = _session.Segments.Count;
            _session.Dispose(); // unsubscribe from the shared transcriber before dropping it
            _session = null;
            _checkpoint?.Stop();
            SetState(LiveState.Idle);
            Status($"Saved. {count} line(s) written to the local repository.");
        }
        catch (Exception ex) { Status("Could not stop: " + Short(ex)); }
    }

    private static string Short(Exception ex) => ex switch
    {
        InvalidOperationException => "the meeting is not active",
        ArgumentException => "the microphone is not available",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => "the data folder is read-only",
        _ => "something went wrong",
    };

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public string? MediaPath => null;
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
        public Task FinishAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    }

    private sealed class FakeTranscriber : ISpeechTranscriber
    {
#pragma warning disable CS0067
        public event Action<Segment>? SegmentReady;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public string AssignTurn(Segment segment) => "spk_1";
    }

    private sealed class WindowDiarizer : ISpeakerDiarizer
    {
        private readonly SherpaDiarizer _inner;
        // Written on the capture pump thread, read on the UI thread.
        private volatile string _current = "spk_1";
        public WindowDiarizer(SherpaDiarizer inner) { _inner = inner; }
        public void Dispose() => _inner.Dispose();
        public void AssignWindowSamples(byte[] pcm16)
        {
            try
            {
                var f = new float[pcm16.Length / 2];
                for (var i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(pcm16, 2 * i) / 32768f;
                _current = _inner.AssignWindow(f);
            }
            catch { }
        }
        public string AssignTurn(Segment segment) => _current;
    }
}
