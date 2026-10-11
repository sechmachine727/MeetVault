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
    private const int WindowBytes = 16000 * 2 * 3; // 3 s of 16 kHz mono
    private SherpaDiarizer? _sherpaDiarizer;
    private UtteranceSegmenter? _utterances;
    private ISpeechTranscriber _transcriber = null!;
    private IAudioCapture _audio = null!;
    private ISpeakerDiarizer _diarizer = null!;
    private string _engineMode = "demo (no models)";
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");
    private long _windowOffsetMs;
    // Adjustment (A): VAD stream time restarts at zero after Reset, so raw utterance times are
    // relative to the current recording segment; this anchors them to the meeting timeline.
    private long _utteranceBaseMs;
    // Adjustment (B): the capture pump can deliver late frames (StopCore joins it with a 1 s
    // timeout) while Pause/Resume/unload mutate the segmenter; every _utterances touch is
    // serialized on this lock.
    private readonly object _vadLock = new();
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
        DropPendingUtterance("unload");
        try { if (_state != LiveState.Idle && _session is not null) await _session.PauseAsync(); } catch { }
        try { await _transcribeGate.WaitAsync(); _transcribeGate.Release(); } catch { } // drain in-flight window
        LiveBridge.SetLive(false); // leaving Live ends active transcription
        try { _capture?.Dispose(); } catch { }
        try { _whisper?.Dispose(); } catch { }
        try { _sherpaDiarizer?.Dispose(); } catch { }
        try { lock (_vadLock) { _utterances?.Dispose(); _utterances = null; } } catch { }
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
        SherpaDiarizer? sherpaDiarizer = null;
        try
        {
            var picked = AppStore.Repo.GetSetting("transcription.model") == "base" ? "ggml-base.bin" : "ggml-tiny.bin";
            var modelFile = File.Exists(Path.Combine(ModelDir, picked)) ? picked : EngineModels.WhisperModel!;
            var (backend, deviceIndex) = TranscriptionDevice.Parse(AppStore.Repo.GetSetting("transcription.device"));
            whisper = new WhisperTranscriber(Path.Combine(ModelDir, modelFile), "auto", backend, deviceIndex);
            capture = new NAudioCapture();
            var vadModel = Path.Combine(ModelDir, "silero_vad.onnx");
            _utterances = File.Exists(vadModel) ? new UtteranceSegmenter(vadModel) : null;
            if (_utterances is null) MeetVault.Core.AppLog.Warn("live: silero_vad.onnx missing; falling back to 3 s windows");
            if (AppStore.Repo.GetSetting("transcription.diarization") != "off")
            {
                sherpaDiarizer = new SherpaDiarizer(Path.Combine(ModelDir, "sherpa-seg.onnx"), Path.Combine(ModelDir, "sherpa-emb.onnx"));
            }
            _whisper = whisper; _capture = capture; _sherpaDiarizer = sherpaDiarizer;
            _transcriber = whisper; _audio = capture; _diarizer = (ISpeakerDiarizer?)sherpaDiarizer ?? diarizer;
            capture.PcmReady += OnPcm;
            capture.CaptureFailed += OnCaptureFailed;
            _modelLabel = modelFile.Replace("ggml-", "").Replace(".bin", "");
            _engineMode = "live: whisper " + _modelLabel + (sherpaDiarizer is null ? ", single speaker" : " + sherpa diarizer");
            return;
        }
        catch
        {
            try { capture?.Dispose(); } catch { }
            try { whisper?.Dispose(); } catch { }
            try { sherpaDiarizer?.Dispose(); } catch { }
            try { lock (_vadLock) { _utterances?.Dispose(); _utterances = null; } } catch { }
            _whisper = null; _capture = null; _sherpaDiarizer = null;
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
            ?? (MicId == "0" ? Loc.T("Dash.WindowsDefault") : Loc.Tf("Live.DeviceN", MicId));
        var id = PendingRecovery.MeetingId;
        PendingRecovery.MeetingId = null;
        if (id is not null) await RecoverAsync(id);
        else SetState(LiveState.Idle); // do not start on navigation; the user presses Start
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reset BEFORE StartAsync: the pump (started inside StartAsync) must never push onto
            // the previous meeting's stream state - neither its VAD stream clock (Reset) nor the
            // rebase base, which would offset every raw utterance time of the new meeting.
            lock (_vadLock) { _utterances?.Reset(); _utteranceBaseMs = 0; }
            _session = new MeetingSession(_audio, _transcriber, _diarizer, new NeutralSpeakerLabeler(), AppStore.Repo);
            await _session.StartAsync(MicId, AppSource, CancellationToken.None);
            WireSession(clearOverlay: true);
            _windowOffsetMs = 0;
            _acceleratorReported = false;
            SetState(LiveState.Recording);
            UpdateEngineStatus(actual: true);
            RebuildSpeakers();
            Status(_engineMode.StartsWith("live") ? Loc.T("Live.Recording") : Loc.T("Live.DemoMode"));
        }
        catch (Exception ex)
        {
            try { if (_session is not null) { _session.MarkFailed(); _session.Dispose(); _session = null; } } catch { }
            SetState(LiveState.Idle);
            MeetVault.Core.AppLog.Error("live: start capture failed", ex);
            Status(Loc.Tf("Live.CouldNotStart", Short(ex)));
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
            _utteranceBaseMs = _windowOffsetMs; // adjustment (A): recovered audio continues on the same timeline
            _acceleratorReported = false;
            SetState(_session.IsPaused ? LiveState.Paused : LiveState.Recording);
            LiveBridge.SetLive(!_session.IsPaused); // paused recovery: the overlay must not claim "Listening…"
            UpdateEngineStatus(actual: true);
            RebuildSpeakers();
            Status(Loc.Tf("Live.ResumedCount", id, _session.Segments.Count));
        }
        catch (Exception ex)
        {
            SetState(LiveState.Idle);
            LiveBridge.SetLive(false); // an exception after WireSession must not leave the overlay claiming "Listening…"
            _capture?.ClearReuse(); // a recovery that failed must not capture the next meeting into the old file
            MeetVault.Core.AppLog.Error("live: resume failed", ex);
            Status(Loc.Tf("Live.CouldNotResumeMeeting", Short(ex)));
        }
    }

    private void WireSession(bool clearOverlay)
    {
        _session!.LiveSegment += OnLiveSegment;
        if (clearOverlay) LiveBridge.Clear();
        LiveBridge.RenameByName = RenameByDisplayName;
        LiveBridge.SetLive(true); // overlay shows "Listening…" instead of looking dead
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
            ? Loc.Tf("Live.AudioIdle", _micLabel)
            : _state == LiveState.Paused ? Loc.Tf("Live.AudioPaused", _micLabel) : Loc.Tf("Live.AudioRecording", _micLabel);

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
        EngineStatus.Text = "live: whisper " + _modelLabel + " + " + accel + note + (_sherpaDiarizer is null ? ", single speaker" : " + sherpa diarizer");
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
            var box = new TextBox { Text = sp.DisplayName, TextWrapping = TextWrapping.NoWrap, PlaceholderText = Loc.T("Live.RenamePlaceholder") };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Loc.Tf("Live.RenameAutomation", sp.DisplayName));
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
        var samples = Pcm16.ToFloats(chunk);
        // The VAD decision, the push and the rebase happen under one lock: a late pump frame
        // must not read _utterances non-null, then lose the lock to OnUnloaded's dispose and
        // dereference the nulled field (an NRE here faults the pump thread and stops capture).
        // The rebase reads _utteranceBaseMs under the same lock that Pause/Resume/Start reset it.
        IReadOnlyList<Utterance>? utterances = null;
        lock (_vadLock)
        {
            if (_utterances is not null)
                utterances = _utterances.Push(samples)
                    .Select(u => new Utterance(u.Samples16k, _utteranceBaseMs + u.StartMs, _utteranceBaseMs + u.EndMs))
                    .ToList();
        }
        if (utterances is null)
        {
            // Fallback: the old fixed 3 s window behavior when the VAD model is absent.
            lock (_window) _window.AddRange(chunk);
            byte[] w;
            lock (_window)
            {
                if (_window.Count < WindowBytes) return;
                w = _window.ToArray();
                _window.Clear();
            }
            // Old semantics preserved: _windowOffsetMs advances the moment the window is cut,
            // even when the transcribe gate later drops it.
            var startMs = _windowOffsetMs;
            var endMs = startMs + w.Length / 32;
            _windowOffsetMs = endMs;
            utterances = new[] { new Utterance(Pcm16.ToFloats(w), startMs, endMs) };
        }
        foreach (var utterance in utterances)
        {
            // Leave the capture pump before the diarizer and whisper run: both are synchronous up
            // to their first await, and running them here would stall the thread that writes the
            // archive, which then drops source audio and loses wall-clock duration.
            await Task.Yield();
            if (!await _transcribeGate.WaitAsync(0))
            {
                // Transcription is behind real time and this utterance cannot wait. The recording
                // still holds the audio; the transcript loses it, so it must be visible in the log.
                var dropped = System.Threading.Interlocked.Increment(ref _droppedWindows);
                MeetVault.Core.AppLog.Warn("live: transcription behind real time, dropped " + ((utterance.EndMs - utterance.StartMs) / 1000.0).ToString("0.0") + "s utterance (total " + dropped + ")");
                continue;
            }
            try
            {
                if (_session?.MeetingId is not { Length: > 0 } meetingId) return;
                // One diarization + one transcription per finalized utterance; the session keeps
                // the resolved speaker for every segment this sentence produces (empty turns
                // stay with the previous one).
                _session.NoteTurnAudio(new TurnAudio(utterance.Samples16k, utterance.StartMs, utterance.EndMs));
                DispatcherQueue.TryEnqueue(() => PartialLine.Text = Loc.T("Live.Transcribing"));
                await _whisper.TranscribeWindowAsync(Pcm16.FromFloats(utterance.Samples16k), utterance.StartMs, meetingId);
                ReportAcceleratorOnce();
            }
            catch { }
            finally
            {
                _transcribeGate.Release();
                DispatcherQueue.TryEnqueue(() => PartialLine.Text = _state != LiveState.Idle ? Loc.T("Live.Listening") : "");
            }
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
            null => Loc.Tf("Live.GpuRuntimeMissing", requested),
            "CPU" => Loc.Tf("Live.GpuNotUsable", requested),
            _ when requested == GpuBackend.Cuda && !compute.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase)
                => Loc.Tf("Live.GpuNativeMismatch", compute, "CUDA"),
            _ when requested == GpuBackend.Vulkan && !compute.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase)
                => Loc.Tf("Live.GpuNativeMismatch", compute, "Vulkan"),
            _ => null,
        };
        if (problem is null) return;
        MeetVault.Core.AppLog.Warn("live: GPU not used as requested; requested=" + requested + " compute=" + (compute ?? "unknown") + " native=" + loaded);
        Status(InfoBarSeverity.Warning, Loc.Tf("Live.GpuFallback", problem));
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
        => DispatcherQueue.TryEnqueue(() => Status(Loc.Tf("Live.CaptureFailed", reason)));

    // Closes any open speech in the segmenter and logs what is dropped, so pause, stop and
    // unload cannot silently lose the pending sentence (the recording still holds the audio).
    private void DropPendingUtterance(string reason)
    {
        lock (_vadLock)
        {
            if (_utterances is null) return;
            var pending = _utterances.Flush();
            long droppedMs = 0;
            foreach (var u in pending) droppedMs += u.EndMs - u.StartMs;
            if (droppedMs > 0) MeetVault.Core.AppLog.Warn("live: " + reason + " dropped " + (droppedMs / 1000.0).ToString("0.0") + "s of pending speech (not transcribed; the recording keeps it)");
            _utterances.Reset();
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state != LiveState.Recording || _session is null) return;
        lock (_window) _window.Clear();
        try
        {
            await _session.PauseAsync();
            // The VAD may hold an open utterance at the pause point; close it and drop it -
            // its audio is not transcribed (the recording keeps it), so the loss is logged.
            // Reset restarts the stream clock for the post-pause segment.
            DropPendingUtterance("pause");
            SetState(LiveState.Paused);
            LiveBridge.SetLive(false); // capture is stopped; the overlay must not claim "Listening…"
            Status(Loc.T("Live.Paused"));
        }
        catch (Exception ex) { Status(Loc.Tf("Live.CouldNotPause", Short(ex))); }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_state != LiveState.Paused || _session is null) return;
        try
        {
            // Adjustment (A): the VAD stream clock restarts at zero after Reset, so raw utterance
            // times would collide with pre-pause timestamps. Anchor the new segment of the timeline
            // at the last transcribed end (the same value the fallback path uses) and reset the
            // segmenter BEFORE capture resumes, so the first post-resume frames start at zero.
            // Base and Reset happen under one lock: OnPcm rebases while holding it, so a late
            // frame can never read a half-updated base/reset pair.
            lock (_vadLock)
            {
                _utteranceBaseMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
                _utterances?.Reset();
            }
            await _session.ResumeAsync();
            _windowOffsetMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            SetState(LiveState.Recording);
            LiveBridge.SetLive(true); // capture is back; the overlay's "Listening…" applies again
            Status(Loc.T("Live.Resumed"));
        }
        catch (Exception ex) { Status(Loc.Tf("Live.CouldNotResume", Short(ex))); }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_state == LiveState.Idle || _session is null) return;
        try
        {
            var confirm = new ContentDialog
            {
                Title = Loc.T("Live.StopTitle"),
                Content = Loc.Tf("Live.StopBody", _session.Segments.Count),
                PrimaryButtonText = Loc.T("Live.StopConfirm"),
                CloseButtonText = Loc.T("Live.KeepRecording"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            DropPendingUtterance("stop");
            await _session.StopAsync();
            var count = _session.Segments.Count;
            _session.Dispose(); // unsubscribe from the shared transcriber before dropping it
            _session = null;
            LiveBridge.SetLive(false);
            _checkpoint?.Stop();
            SetState(LiveState.Idle);
            Status(Loc.Tf("Live.Saved", count));
        }
        catch (Exception ex) { Status(Loc.Tf("Live.CouldNotStop", Short(ex))); }
    }

    private static string Short(Exception ex) => ex switch
    {
        InvalidOperationException => Loc.T("Live.ErrNotActive"),
        ArgumentException => Loc.T("Live.ErrMicUnavailable"),
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => Loc.T("Live.ErrReadOnly"),
        _ => Loc.T("Live.ErrGeneric"),
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
        public SpeakerHint AssignTurn(TurnAudio turn) => new("spk_1", SpeakerOrigin.Unknown, null);
    }
}
