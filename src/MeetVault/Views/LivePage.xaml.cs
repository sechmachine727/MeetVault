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
    private IStreamingTranscriber? _streaming;      // the streaming engine (Vosk small)
    private readonly System.Diagnostics.Stopwatch _streamWatch = new();
    private NAudioCapture? _capture;
    private ISpeechTranscriber _transcriber = null!;
    private IAudioCapture _audio = null!;
    private string _engineMode = "demo (no models)";
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");
    // The stream timeline restarts after every pause; this anchors raw engine times to the
    // meeting timeline (nowMs = base + elapsed since the watch restarted).
    private long _utteranceBaseMs;
    // Index of the OPEN SENTENCE's row in Lines: the interim text renders as a NORMAL
    // transcript row — same template as finals — and the final replaces it in place, so
    // the current sentence never appears as a special caption. -1 = no open row.
    private int _partialRowIndex = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _checkpoint;
    private LiveState _state = LiveState.Idle;
    private string _micLabel = "not started";

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
        try { if (_state != LiveState.Idle && _session is not null) await _session.PauseAsync(); } catch { }
        try { if (_state != LiveState.Idle) _streaming?.Flush(); } catch { } // close pending speech (pause already flushed; safety no-op)
        LiveBridge.SetLive(false); // leaving Live ends active transcription
        try { _capture?.Dispose(); } catch { }
        try { _streaming?.Dispose(); } catch { }
    }

    // The engine resolves at load; recording starts only on an explicit action or a resume.
    private void ResolveEngines()
    {
        ISpeechTranscriber transcriber = new FakeTranscriber();
        IAudioCapture audio = new FakeAudioCapture();
        _engineMode = "demo (add models to run live)";
        // The selected language × size; unknown/missing values resolve to the default
        // (English/small), so a legacy install whose transcription.model still names an
        // older model keeps working (the legacy id is the third argument).
        var repo = AppStore.Repo;
        var option = MeetVault.Core.EngineCatalog.Resolve(
            repo.GetSetting("transcription.language"),
            repo.GetSetting("transcription.size"),
            repo.GetSetting("transcription.model"));
        if (!EngineModels.CanRun(option))
        {
            // Prefer an installed model of the same language (small first), then any installed.
            var fallback =
                MeetVault.Core.EngineCatalog.Options.FirstOrDefault(o => o.Language == option.Language && EngineModels.CanRun(o))
                ?? MeetVault.Core.EngineCatalog.Options.FirstOrDefault(o => EngineModels.CanRun(o));
            if (fallback is not null)
            {
                MeetVault.Core.AppLog.Warn("live: model " + option.Model + " missing; falling back to " + fallback.Model);
                option = fallback;
            }
        }
        if (!EngineModels.CanRun(option))
        {
            _transcriber = transcriber; _audio = audio; // demo (no models)
            MeetVault.Core.AppLog.Info("live: engines resolved -> " + _engineMode);
            EngineStatus.Text = _engineMode;
            return;
        }

        VoskStreamingTranscriber? vosk = null;
        NAudioCapture? capture = null;
        try
        {
            vosk = new VoskStreamingTranscriber(
                Path.Combine(ModelDir, "vosk", option.Model),
                () => _utteranceBaseMs + (long)_streamWatch.Elapsed.TotalMilliseconds);
            vosk.PartialReady += OnPartial;
            _streaming = vosk;
            _transcriber = vosk;
            _engineMode = "live: vosk " + option.Model;
            capture = new NAudioCapture();
            capture.PcmReady += OnPcm;
            capture.CaptureFailed += OnCaptureFailed;
            _capture = capture;
            _audio = capture;
        }
        catch
        {
            try { vosk?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            _streaming = null; _transcriber = transcriber; _audio = audio;
            _engineMode = "demo (engine init failed)";
            MeetVault.Core.AppLog.Error("live: engine init failed; running demo");
        }
        MeetVault.Core.AppLog.Info("live: engines resolved -> " + _engineMode);
        EngineStatus.Text = _engineMode;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveEngines();
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
            // Reset before StartAsync: the pump (started inside StartAsync) must never push
            // onto the previous meeting's stream state — the rebase base is zeroed, and a
            // stale open-sentence row from a previous meeting must not be reused.
            _utteranceBaseMs = 0;
            _streamWatch.Restart(); // streaming timeline: nowMs = base + elapsed since start
            _partialRowIndex = -1;
            _session = new MeetingSession(_audio, _transcriber, AppStore.Repo);
            await _session.StartAsync(MicId, AppSource, CancellationToken.None);
            WireSession(clearOverlay: true);
            SetState(LiveState.Recording);
            EngineStatus.Text = _engineMode;
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
            _session = await MeetingSession.RecoverAsync(id, _audio, _transcriber, AppStore.Repo, MicId, AppSource);
            _partialRowIndex = -1; // backlog lines are finals; none of them is an open sentence
            foreach (var s in _session.Segments) AddLine(s.Text);
            WireSession(clearOverlay: false); // keep the recovered backlog for a late-opened overlay
            _utteranceBaseMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            _streamWatch.Restart(); // active recovery restarts capture: stream timeline starts here
            SetState(_session.IsPaused ? LiveState.Paused : LiveState.Recording);
            LiveBridge.SetLive(!_session.IsPaused); // paused recovery: the overlay must not claim "Listening…"
            EngineStatus.Text = _engineMode;
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

    // The recognizer consumes the raw 16 kHz stream (the runner enqueues; the engine
    // decodes on its own thread). Interim text arrives through PartialReady and finals
    // through the session's usual SegmentReady path.
    private void OnPcm(byte[] chunk)
    {
        var streaming = _streaming;
        if (streaming is not null && streaming.IsRunning) streaming.Push(Pcm16.ToFloats(chunk));
    }

    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() => AddLine(segment.Text));

    // Streaming engines publish the open utterance's interim text. It shows up as a normal
    // transcript row (updated in place, replaced by the final when the sentence closes);
    // an empty string clears the open row. The overlay keeps its own dimmed partial line.
    private void OnPartial(string text) => DispatcherQueue.TryEnqueue(() =>
    {
        if (text.Length > 0)
        {
            var live = new TranscriptLine("", text);
            if (_partialRowIndex >= 0 && _partialRowIndex < Lines.Count) Lines[_partialRowIndex] = live;
            else { Lines.Add(live); _partialRowIndex = Lines.Count - 1; }
        }
        else ClearPartialRow();
        LiveBridge.NotifyPartial(text);
    });

    private void ClearPartialRow()
    {
        if (_partialRowIndex >= 0 && _partialRowIndex < Lines.Count) Lines.RemoveAt(_partialRowIndex);
        _partialRowIndex = -1;
    }

    private void AddLine(string text)
    {
        // The open sentence's row BECOMES its final: no duplicated row, no restyle.
        if (_partialRowIndex >= 0)
        {
            if (_partialRowIndex < Lines.Count) Lines[_partialRowIndex] = new TranscriptLine("", text);
            else Lines.Add(new TranscriptLine("", text));
            _partialRowIndex = -1;
        }
        else Lines.Add(new TranscriptLine("", text));
        LiveBridge.Publish("", text);
    }

    private void Status(string message) => Status(InfoBarSeverity.Informational, message);

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // Capture died on its own. The meeting is still open so the user can save what was
    // recorded, but they must not be left believing the microphone is still live: clear
    // the overlay's live state too (its empty conversation must stop saying "Listening…"),
    // not only the page's status line.
    private void OnCaptureFailed(string reason)
        => DispatcherQueue.TryEnqueue(() =>
        {
            LiveBridge.SetLive(false);
            Status(Loc.Tf("Live.CaptureFailed", reason));
        });

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state != LiveState.Recording || _session is null) return;
        try
        {
            await _session.PauseAsync();
            try { _streaming?.Flush(); } catch { } // the session's StopAsync already flushed; safety no-op
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
            // The stream timeline restarts with capture: anchor the new segment at the last
            // transcribed end so raw engine times continue on the meeting timeline.
            _utteranceBaseMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            _streamWatch.Restart();
            await _session.ResumeAsync();
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
            await _session.StopAsync();
            try { _streaming?.Flush(); } catch { } // the session's StopAsync already flushed; safety no-op
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
}
