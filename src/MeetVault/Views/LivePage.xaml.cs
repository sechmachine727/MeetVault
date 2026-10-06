using System.Collections.ObjectModel;
using MeetVault.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();

    private MeetingSession _session = null!;
    private WhisperTranscriber? _whisper;
    private NAudioCapture? _capture;
    private readonly List<byte> _window = new();
    private WindowDiarizer? _windowDiarizer;
    private ISpeechTranscriber _transcriber = null!;
    private IAudioCapture _audio = null!;
    private ISpeakerDiarizer _diarizer = null!;
    private string _engineMode = "demo (no models)";
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");
    private long _windowOffsetMs;
    private readonly Dictionary<string, string> _speakerNames = new();
    private string _micLabel = "not started";
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _checkpoint;
    private bool _started;
    private static string MicId => AppStore.Repo.GetSetting("audio.mic") ?? "0";

    public LivePage()
    {
        InitializeComponent();
        // Array snapshots cross the ABI cleanly; refresh on change.
        TranscriptList.ItemsSource = Lines.ToArray();
        Lines.CollectionChanged += (_, _) => { TranscriptList.ItemsSource = Lines.ToArray(); TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Leaving Live must stop capture: otherwise the mic and transcriber keep running invisibly.
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _checkpoint?.Stop();
        LiveBridge.RenameByName = null;
        // Pause, not stop: navigating away must not silently finalize the meeting.
        // The draft stays recoverable and Dashboard offers it again.
        try { if (_started && _session is not null) await _session.PauseAsync(); } catch { }
        // Drain the in-flight window before the native model is freed.
        try { await _transcribeGate.WaitAsync(); _transcribeGate.Release(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _whisper?.Dispose(); } catch { }
        try { _windowDiarizer?.Dispose(); } catch { }
    }

    // Engines resolve at load, not navigate: a native fault here must not break navigation.
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
            var wantGpu = AppStore.Repo.GetSetting("transcription.device") == "gpu";
            try { whisper = new WhisperTranscriber(Path.Combine(ModelDir, modelFile), "auto", wantGpu); }
            catch { whisper = new WhisperTranscriber(Path.Combine(ModelDir, modelFile)); }
            capture = new NAudioCapture();
            if (AppStore.Repo.GetSetting("transcription.diarization") != "off")
            {
                var dz = new SherpaDiarizer(Path.Combine(ModelDir, "sherpa-seg.onnx"), Path.Combine(ModelDir, "sherpa-emb.onnx"));
                windowDiarizer = new WindowDiarizer(dz);
            }
            // Publish only once every component is up, so a late throw cannot leave a
            // recording mic with no transcription pipeline.
            _whisper = whisper; _capture = capture; _windowDiarizer = windowDiarizer;
            _transcriber = whisper; _audio = capture; _diarizer = (ISpeakerDiarizer?)windowDiarizer ?? diarizer;
            capture.PcmReady += OnPcm;
            _engineMode = "live: whisper " + modelFile.Replace("ggml-", "").Replace(".bin", "") + (whisper.UseGpu ? " + CUDA" : "") + (windowDiarizer is null ? ", single speaker" : " + sherpa diarizer");
            return;
        }
        catch
        {
            try { capture?.Dispose(); } catch { }
            try { whisper?.Dispose(); } catch { }
            try { windowDiarizer?.Dispose(); } catch { }
            _whisper = null; _capture = null; _windowDiarizer = null;
            _engineMode = "demo (engine init failed)";
        }
        _transcriber = transcriber; _audio = audio; _diarizer = diarizer;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveEngines();
        EngineStatus.Text = _engineMode;
        try
        {
            var id = PendingRecovery.MeetingId;
            PendingRecovery.MeetingId = null;
            if (id is not null)
            {
                _session = await MeetingSession.RecoverAsync(id, _audio, _transcriber, _diarizer, new NeutralSpeakerLabeler(), AppStore.Repo, MicId, "");
                foreach (var sp in _session.Speakers) _speakerNames[sp.Id] = sp.DisplayName;
                foreach (var s in _session.Segments) AddLine(s.SpeakerId, s.Text);
                Status($"Recovered {_session.Segments.Count} line(s) from the interrupted meeting.");
            }
            else
            {
                // No local gender classifier ships, so labels are neutral "Speaker N" (FR9: no fake estimate).
                _session = new MeetingSession(_audio, _transcriber, _diarizer, new NeutralSpeakerLabeler(), AppStore.Repo);
                await _session.StartAsync(MicId, "", CancellationToken.None);
            }
            _session.LiveSegment += OnLiveSegment;
            if (id is null) LiveBridge.Clear(); // keep the recovered backlog for a late-opened overlay
            LiveBridge.RenameByName = RenameByDisplayName;
            // Recovery continues an existing timeline; without this the new windows
            // restart at 0 ms and their ids collide with the recovered segments.
            _windowOffsetMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            _started = true;
            StartCheckpointTimer();
            _micLabel = NAudioCapture.ListDevices().FirstOrDefault(d => d.Id.ToString() == MicId).Name
                ?? (MicId == "0" ? "Windows default" : "device " + MicId);
            RebuildSpeakers();
            RefreshAudioStatus();
            Status(_engineMode.StartsWith("live") ? "Recording. Speaks turns appear below as they are recognized." : "Demo mode: add Whisper and diarizer models to models/ for live transcription.");
        }
        catch (Exception ex)
        {
            // A draft row was persisted before capture started; mark it failed so it
            // never lingers as a finalizable empty meeting.
            try { if (_session is not null) { _session.MarkFailed(); _session = null!; } } catch { }
            Status("Could not start capture. Check the microphone in Settings, then reopen Live. (" + Short(ex) + ")");
            RefreshAudioStatus();
        }
    }

    // FR6: persist accrued lines periodically so a crash loses at most one interval.
    private void StartCheckpointTimer()
    {
        _checkpoint ??= DispatcherQueue.CreateTimer();
        _checkpoint.Interval = TimeSpan.FromSeconds(15);
        _checkpoint.Tick += async (_, _) => { try { await _session.CheckpointAsync(); } catch { } };
        _checkpoint.Start();
    }

    private void RefreshAudioStatus()
        => AudioStatus.Text = _audio.IsCapturing ? $"Recording from {_micLabel}." : $"Mic: {_micLabel}.";

    // Speakers side panel: one editable row per speaker; Enter or focus-out renames everywhere.
    private void RebuildSpeakers()
    {
        SpeakerPanel.Children.Clear();
        foreach (var sp in _session.Speakers)
        {
            _speakerNames[sp.Id] = sp.DisplayName;
            var id = sp.Id;
            var box = new TextBox { Text = sp.DisplayName, Header = sp.Id, TextWrapping = TextWrapping.NoWrap };
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
        if (trimmed.Length == 0 || !_speakerNames.TryGetValue(speakerId, out var old) || old == trimmed) return;
        _session.RenameSpeaker(speakerId, trimmed);
        _speakerNames[speakerId] = trimmed;
        for (var i = 0; i < Lines.Count; i++)
            if (Lines[i].Speaker == old) Lines[i].Speaker = trimmed;
        TranscriptList.ItemsSource = Lines.ToArray();
    }

    // ~10 s windows of mic PCM transcribed as they finalize.
    private async void OnPcm(byte[] chunk)
    {
        if (_whisper is null || !_whisper.IsRunning) return;
        lock (_window) _window.AddRange(chunk);
        // 6 s windows: shorter first-line latency than the old 10 s while keeping enough context.
        if (_window.Count < 16000 * 2 * 6) return;
        byte[] window;
        lock (_window) { window = _window.ToArray(); _window.Clear(); }
        // Advance the timeline by the samples actually consumed, not a fixed guess.
        var windowMs = window.Length / 32; // 16000 samples/s * 2 bytes = 32 bytes/ms
        // OnPcm is serialized by NAudio, so the timeline advances on the capture thread;
        // a dropped window still consumes audio time, keeping labels aligned to the WAV.
        var start = _windowOffsetMs;
        _windowOffsetMs = start + windowMs;
        // One window in flight at a time, and never queue: with a slower-than-realtime
        // model a queue would grow for the whole session and the transcript would fall
        // ever further behind (NFR2). Dropping the newest window keeps it live; the WAV
        // on disk still holds every sample.
        if (!await _transcribeGate.WaitAsync(0)) return;
        try
        {
            // No meeting id (stopped/ended): skip rather than mint a shared "live-" id.
            if (_session.MeetingId is not { Length: > 0 } meetingId) return;
            _windowDiarizer?.AssignWindowSamples(window);
            DispatcherQueue.TryEnqueue(() => PartialLine.Text = "Transcribing\u2026");
            // Segment ids are namespaced by the real meeting id (the table's PK is global).
            await _whisper.TranscribeWindowAsync(window, start, meetingId);
        }
        catch { /* next window retries; audio file keeps everything */ }
        finally
        {
            _transcribeGate.Release();
            DispatcherQueue.TryEnqueue(() => PartialLine.Text = _audio.IsCapturing ? "Listening\u2026" : "");
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

    // A rename coming from the overlay: resolve the display name back to a speaker id.
    private bool RenameByDisplayName(string oldName, string newName)
    {
        var id = _speakerNames.FirstOrDefault(kv => kv.Value == oldName).Key;
        if (id is null) return false;
        Rename(id, newName);
        return true;
    }

    private void Status(string message) { StatusBar.Message = message; StatusBar.IsOpen = true; }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        lock (_window) _window.Clear();
        try { await _session.PauseAsync(); Status("Paused. Audio in the gap is not recorded and will not reappear on resume."); }
        catch (Exception ex) { Status("Nothing to pause: " + Short(ex)); }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.ResumeAsync();
            _windowOffsetMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            RefreshAudioStatus();
            Status("Resumed. Capture continues after the gap.");
        }
        catch (Exception ex) { Status("Nothing to resume: " + Short(ex)); }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is null) { Status("Nothing to stop."); return; }
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
            RefreshAudioStatus();
            Status($"Saved. {_session.Segments.Count} line(s) written to the local repository.");
        }
        catch (Exception ex) { Status("Could not stop: " + Short(ex)); }
    }

    // One plain sentence for the user; the full exception stays out of the UI.
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
    }

    private sealed class FakeTranscriber : ISpeechTranscriber
    {
#pragma warning disable CS0067 // interface contract; demo mode never raises it
        public event Action<Segment>? SegmentReady;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; } // no models: emit nothing, never a fake line into the record
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public string AssignTurn(Segment segment) => "spk_1";
    }

    // Bridges window-level sherpa assignment into the session's per-segment path.
    private sealed class WindowDiarizer : ISpeakerDiarizer
    {
        private readonly SherpaDiarizer _inner;
        private string _current = "spk_1";
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
