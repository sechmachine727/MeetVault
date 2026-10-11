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
    // The engine option the page resolved at load (ResolveEngines); Start's lazy init uses it.
    private MeetVault.Core.EngineOption? _resolvedOption;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _checkpoint;
    private LiveState _state = LiveState.Idle;
    private string _micLabel = "not started";

    // Language control (auto-detect). The relay swaps the ONE active engine under the
    // running session; the monitor detects spoken language from the same PCM and
    // suggests switches in auto mode. Null in demo mode.
    private SwitchableStreamingTranscriber? _relay;
    private MeetVault.Core.LangIdModel? _langIdModel;
    private LanguageMonitor? _langMonitor;
    private string _activeLanguage = "en";
    private bool _autoMode;
    private string? _pendingLanguage;               // auto switch deferred to the next endpoint
    private long _pendingSinceMs;
    private bool _suppressLanguageEvent;            // combo <-> setting feedback guard

    // Screen recording: one controller per page; runs follow the meeting lifecycle.
    private MeetVault.Core.ScreenCaptureController? _screen;
    // Kept so page teardown can release the native recorder too (review r6).
    private MeetVault.Core.ScreenRecorderLibAdapter? _adapter;
    private readonly List<MeetVault.Core.CaptureSource> _screenSources = new();
    private bool _suppressScreenEvent;
    private Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource? _previewSink;

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
        LiveBridge.LanguageRequested += OnLanguageRequested;
        LiveBridge.ScreenToggleRequested += OnScreenToggleRequested;
    }

    // Leaving Live pauses an active meeting; a draft stays recoverable on the Dashboard.
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _checkpoint?.Stop();
        try { if (_state != LiveState.Idle && _session is not null) await _session.PauseAsync(); } catch { }
        try { if (_state != LiveState.Idle) _streaming?.Flush(); } catch { } // close pending speech (pause already flushed; safety no-op)
        LiveBridge.SetLive(false); // leaving Live ends active transcription
        LiveBridge.SetMeetingState(_state == LiveState.Idle ? LiveBridge.MeetingState.Idle : LiveBridge.MeetingState.Paused);
        LiveBridge.LanguageRequested -= OnLanguageRequested;
        LiveBridge.ScreenToggleRequested -= OnScreenToggleRequested;
        try { _capture?.Dispose(); } catch { }
        try { _streaming?.Dispose(); } catch { }
        try { _langMonitor?.Dispose(); } catch { } _langMonitor = null;
        try { _langIdModel?.Dispose(); } catch { } _langIdModel = null;
        if (_screen is not null)
        {
            // Leaving Live pauses the meeting: the screen run finalizes and its track is
            // persisted; the controller is page-scoped and goes with the page.
            try { await _screen.MeetingPausedAsync(); } catch { }
            PersistFinishedTracks();
            LiveBridge.SetScreenState(false);
            try { _screen.Dispose(); } catch { }
            try { _adapter?.Dispose(); } catch { } // releases the native recorder (review r6)
            _screen = null;
            _adapter = null;
        }
        _relay = null;
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
        // One shared resolution (EngineModels.ResolveLive) — Dashboard and Live must
        // agree on the effective model; the fallback preference is same-language small
        // first, then any installed.
        var resolution = EngineModels.ResolveLive(repo);
        var option = resolution.Option;
        _resolvedOption = option;
        if (resolution.IsFallback)
            MeetVault.Core.AppLog.Warn("live: model " + resolution.Requested.Model + " missing; falling back to " + option.Model);
        if (resolution.Demo)
        {
            _transcriber = transcriber; _audio = audio; // demo (no models)
            MeetVault.Core.AppLog.Info("live: engines resolved -> " + _engineMode);
            EngineStatus.Text = _engineMode;
            return;
        }

        NAudioCapture? capture = null;
        try
        {
            // Auto-detect only runs with the language-ID model staged; without it the
            // configured language applies from the first second.
            var autoRequested = string.Equals(repo.GetSetting("transcription.language"), "auto", StringComparison.OrdinalIgnoreCase);
            _autoMode = autoRequested && MeetVault.Core.LangIdModel.IsAvailable(ModelDir);
            var now = () => _utteranceBaseMs + (long)_streamWatch.Elapsed.TotalMilliseconds;
            _relay = new SwitchableStreamingTranscriber(dir => new VoskStreamingTranscriber(dir, now));
            // The engine is created lazily — on the first Start (or on the first detection in
            // auto mode). Opening the Live tab must not load a model: the large tier takes
            // seconds, and the page disposes the relay on every navigation.
            _activeLanguage = option.Language;
            _relay.PartialReady += OnPartial;
            _streaming = _relay;
            _transcriber = _relay;
            _engineMode = "live: vosk " + option.Model;
            if (_autoMode) EnsureLangMonitor();
            capture = new NAudioCapture();
            capture.PcmReady += OnPcm;
            capture.CaptureFailed += OnCaptureFailed;
            _capture = capture;
            _audio = capture;
            _adapter = new MeetVault.Core.ScreenRecorderLibAdapter();
            _screen = new MeetVault.Core.ScreenCaptureController(
                _adapter,
                () => AppStore.MediaDir,
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ResolveScreenSource,
                MeetVault.Core.CaptureSourceResolver.FindWindowHandle,
                MeetVault.Core.CaptureSourceResolver.IsWindowAlive);
            _screen.Enabled = AppStore.Repo.GetSetting("screen.enabled") != "false";
            _screen.Warning += message => DispatcherQueue.TryEnqueue(() => { Status(message); RefreshScreenStatus(); });
            _screen.FramePreview += OnScreenFrame;
        }
        catch (Exception ex)
        {
            try { _relay?.Dispose(); } catch { } _relay = null;
            try { _langMonitor?.Dispose(); } catch { } _langMonitor = null;
            try { _langIdModel?.Dispose(); } catch { } _langIdModel = null;
            try { capture?.Dispose(); } catch { }
            try { _adapter?.Dispose(); } catch { } _adapter = null;
            _streaming = null; _transcriber = transcriber; _audio = audio;
            _engineMode = "demo (engine init failed)";
            MeetVault.Core.AppLog.Error("live: engine init failed; running demo", ex);
        }
        MeetVault.Core.AppLog.Info("live: engines resolved -> " + _engineMode);
        EngineStatus.Text = _engineMode;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveEngines();
        BuildLanguageCombo((AppStore.Repo.GetSetting("transcription.language") ?? "en").Trim().ToLowerInvariant());
        InitScreenSection();
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
            // Lazy engine init: the configured model loads on the first Start (off the UI
            // thread — the large tier takes seconds), not when the tab is opened.
            if (_relay is not null && !_autoMode && _relay.ActiveModelDir is null)
            {
                var option = _resolvedOption ?? EngineCatalog.Find(_activeLanguage, "small");
                if (option is null || !await Task.Run(() => _relay.SwapTo(Path.Combine(ModelDir, "vosk", option.Model))))
                    throw new InvalidOperationException("engine init failed for " + (option?.Model ?? _activeLanguage));
            }
            if (_autoMode) EnsureLangMonitor();
            _session = new MeetingSession(_audio, _transcriber, AppStore.Repo);
            await _session.StartAsync(MicId, AppSource, CancellationToken.None);
            WireSession(clearOverlay: true);
            SetState(LiveState.Recording);
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Recording);
            EngineStatus.Text = _engineMode;
            Status(_engineMode.StartsWith("live") ? Loc.T("Live.Recording") : Loc.T("Live.DemoMode"));
            if (_screen is not null && _session.MeetingId is { } screenMid)
            {
                // Screen start follows SetMeetingState(Recording) above; failures here must
                // never block audio/transcription (soft requirement of E1).
                try { await _screen.MeetingStartedAsync(screenMid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: start hook failed: " + ex.Message); }
                PersistFinishedTracks();
            }
            RefreshScreenStatus();
            if (_autoMode && _langMonitor is not null && _relay is not null && _relay.ActiveModelDir is null)
            {
                // Auto: collect the first speech windows, then transcribe on the detected
                // language — or English/small when no speech is detected at all.
                Status(Loc.T("Live.LangDetecting"));
                await EnsureLangIdLoaded();
                // The gate needs a full 4 s window plus inference before any detection can
                // exist; 3.5 s deterministically lost that race, so the auto start always
                // fell through to en/small (review r5).
                await Task.Delay(6500);
                if (_relay.ActiveModelDir is null)
                {
                    var fallback = EngineCatalog.Find("en", "small");
                    if (fallback is not null && _relay.SwapTo(Path.Combine(ModelDir, "vosk", fallback.Model)))
                    {
                        _activeLanguage = "en";
                        _engineMode = "live: vosk " + fallback.Model;
                        _langMonitor.SetActive("en");
                        if (_session?.MeetingId is { } mid) AppStore.Repo.SetMeetingLanguage(mid, "en");
                        LiveBridge.SetLanguage(new LiveBridge.LanguageInfo("en", true, false));
                        EngineStatus.Text = _engineMode;
                        MeetVault.Core.AppLog.Info("langid: no speech in 3.5 s; starting en/small (fallback)");
                    }
                }
                Status(Loc.T("Live.Recording"));
            }
            else if (_relay is not null && _relay.ActiveModelDir is not null)
            {
                // Manual (or preloaded) engine: record which language the meeting runs.
                if (_session?.MeetingId is { } mid) AppStore.Repo.SetMeetingLanguage(mid, _activeLanguage);
                LiveBridge.SetLanguage(new LiveBridge.LanguageInfo(_activeLanguage, _autoMode, false));
            }
            SetLangStatus();
        }
        catch (Exception ex)
        {
            try { if (_session is not null) { _session.MarkFailed(); _session.Dispose(); _session = null; } } catch { }
            SetState(LiveState.Idle);
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Idle);
            MeetVault.Core.AppLog.Error("live: start capture failed", ex);
            Status(Loc.Tf("Live.CouldNotStart", Short(ex)));
        }
    }

    // The language monitor is built wherever AUTO becomes relevant: at load when the
    // persisted setting is auto, and lazily when the user picks Auto at runtime (review r5:
    // a manual-loaded page that switched to Auto kept _langMonitor null, so Start neither
    // collected nor transcribed anything).
    private void EnsureLangMonitor()
    {
        if (_langMonitor is not null || _relay is null) return;
        _langMonitor = new LanguageMonitor(window =>
        {
            var model = _langIdModel;
            return model?.Detect(window);
        });
        _langMonitor.SetActive(_activeLanguage);
        _langMonitor.Detected += OnLangDetected;
        _langMonitor.Suggested += OnLangSuggested;
    }

    // The 86 MB detector session has no business loading when the tab opens; it loads on
    // the first Start/Recover in auto mode (off the UI thread).
    private async Task EnsureLangIdLoaded()
    {
        if (_langIdModel is not null) return;
        try { _langIdModel = await Task.Run(() => MeetVault.Core.LangIdModel.Load(ModelDir)); }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("langid: detector load failed: " + ex.Message); }
    }

    private async Task RecoverAsync(string id)
    {
        try
        {
            if (AppStore.Repo.Load(id)?.MediaPath is { Length: > 0 } mediaPath && _capture is not null) _capture.ReuseFile(mediaPath);
            _session = await MeetingSession.RecoverAsync(id, _audio, _transcriber, AppStore.Repo, MicId, AppSource);
            foreach (var s in _session.Segments) AddLine(s.Text);
            WireSession(clearOverlay: false); // keep the recovered backlog for a late-opened overlay
            _utteranceBaseMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
            _streamWatch.Restart(); // active recovery restarts capture: stream timeline starts here
            SetState(_session.IsPaused ? LiveState.Paused : LiveState.Recording);
            LiveBridge.SetLive(!_session.IsPaused); // paused recovery: the overlay must not claim "Listening…"
            LiveBridge.SetMeetingState(_session.IsPaused ? LiveBridge.MeetingState.Paused : LiveBridge.MeetingState.Recording);
            // The meeting's own language comes back with it — the persisted code is the
            // engine the draft was last captured with (auto mode included).
            if (_autoMode) await EnsureLangIdLoaded();
            var storedLanguage = AppStore.Repo.Load(id)?.Language;
            if (_relay is not null)
            {
                if (!string.IsNullOrEmpty(storedLanguage)) ApplyLanguage(storedLanguage!, auto: _autoMode, reason: "resume");
                else if (!_autoMode) ApplyLanguage(_activeLanguage, auto: false, reason: "resume");
                else LiveBridge.SetLanguage(new LiveBridge.LanguageInfo(_activeLanguage, true, false));
            }
            EngineStatus.Text = _engineMode;
            if (_screen is not null && _session.MeetingId is { } recMid)
            {
                try
                {
                    // A recovered draft must rebind the controller, or its phase stays Idle
                    // and no screen run can ever start for this meeting again (review r6).
                    // The anchor subtracts the already-recorded audio, so new runs stay on
                    // the recorded timeline; a paused draft binds paused and opens no run.
                    var recordedMs = _session.Segments.Count == 0 ? 0 : _session.Segments.Max(s => s.EndMs);
                    await _screen.MeetingStartedAsync(recMid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - recordedMs, _session.IsPaused);
                    PersistFinishedTracks();
                    RefreshScreenStatus();
                }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: recover bind failed: " + ex.Message); }
            }
            Status(Loc.Tf("Live.ResumedCount", id, _session.Segments.Count));
        }
        catch (Exception ex)
        {
            SetState(LiveState.Idle);
            LiveBridge.SetLive(false); // an exception after WireSession must not leave the overlay claiming "Listening…"
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Idle);
            _capture?.ClearReuse(); // a recovery that failed must not capture the next meeting into the old file
            MeetVault.Core.AppLog.Error("live: resume failed", ex);
            Status(Loc.Tf("Live.CouldNotResumeMeeting", Short(ex)));
        }
    }

    private void WireSession(bool clearOverlay)
    {
        _session!.LiveSegment += OnLiveSegment;
        if (clearOverlay) LiveBridge.Clear();
        // Demo mode has no engine: the overlay must not claim "Listening…" when nothing
        // can transcribe. It opens fine for reading; the live state stays off.
        if (_streaming is not null) LiveBridge.SetLive(true);
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
        var samples = Pcm16.ToFloats(chunk);
        var streaming = _streaming;
        if (streaming is not null && streaming.IsRunning) streaming.Push(samples);
        _langMonitor?.Push(samples);   // detection rides the same capture stream
    }

    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() =>
        {
            AddLine(segment.Text);
            // An endpoint just closed: a pending auto language switch applies now.
            TryApplyPendingLanguage();
        });

    // Streaming engines publish the open utterance's interim text; it renders in the trailing
    // PartialLine under the list — NOT in the bound collection, whose change handler
    // re-snapshots ItemsSource: per-partial mutations rebuilt every container and stalled the
    // UI thread (recognizer partials arrive several times a second).
    private void OnPartial(string text) => DispatcherQueue.TryEnqueue(() =>
    {
        if (text.Length > 0)
        {
            PartialLine.Text = text;
            PartialLine.Visibility = Visibility.Visible;
            TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
        }
        else ClearPartialRow();
        LiveBridge.NotifyPartial(text);
    });

    private void ClearPartialRow()
    {
        PartialLine.Text = "";
        PartialLine.Visibility = Visibility.Collapsed;
    }

    private void AddLine(string text)
    {
        ClearPartialRow(); // the final supersedes the open sentence
        Lines.Add(new TranscriptLine("", text));
        LiveBridge.Publish("", text);
    }

    // --- Screen recording (E1) ---

    private void InitScreenSection()
    {
        RefreshScreenSources();
        if (_screen is null)
        {
            ScreenToggle.IsEnabled = false;
            ScreenSourceCombo.IsEnabled = false;
            ScreenRunButton.IsEnabled = false;
            ScreenStatus.Text = Loc.T("Live.ScreenUnavailable");
            return;
        }
        _suppressScreenEvent = true;
        try { ScreenToggle.IsOn = _screen.Enabled; }
        finally { _suppressScreenEvent = false; }
        ScreenToggle.Toggled += ScreenToggle_Toggled;
        RefreshScreenStatus();
    }

    private void RefreshScreenSources()
    {
        _screenSources.Clear();
        _screenSources.AddRange(MeetVault.Core.CaptureSourceResolver.Displays());
        _screenSources.AddRange(MeetVault.Core.CaptureSourceResolver.Windows());
        _suppressScreenEvent = true;
        try
        {
            ScreenSourceCombo.Items.Clear();
            foreach (var s in _screenSources) ScreenSourceCombo.Items.Add(s.Label);
            var stored = (AppStore.Repo.GetSetting("screen.source") ?? "").Trim();
            var idx = 0;
            if (stored.Length > 0)
                for (var i = 0; i < _screenSources.Count; i++)
                    if (MatchesStored(stored, _screenSources[i])) { idx = i; break; }
            ScreenSourceCombo.SelectedIndex = idx;
        }
        finally { _suppressScreenEvent = false; }
    }

    // Stored-form or retitled-window prefix match, mirroring CaptureSourceResolver.Resolve.
    private static bool MatchesStored(string stored, MeetVault.Core.CaptureSource s)
    {
        if (string.Equals(MeetVault.Core.CaptureSourceResolver.Format(s), stored, StringComparison.OrdinalIgnoreCase)) return true;
        if (!stored.StartsWith("window:", StringComparison.OrdinalIgnoreCase) || s.Kind != "window") return false;
        var rest = stored[7..];
        var sep = rest.IndexOf(':');
        var exe = sep >= 0 ? rest[..sep] : rest;
        var title = sep >= 0 ? rest[(sep + 1)..] : "";
        var sTitle = MeetVault.Core.CaptureSourceResolver.LabelTitle(s);
        return string.Equals(s.Id, exe, StringComparison.OrdinalIgnoreCase) &&
               (title.Length == 0 || sTitle.StartsWith(title, StringComparison.Ordinal) || title.StartsWith(sTitle, StringComparison.Ordinal));
    }

    private void ScreenSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressScreenEvent || ScreenSourceCombo.SelectedIndex < 0 || ScreenSourceCombo.SelectedIndex >= _screenSources.Count) return;
        AppStore.Repo.SetSetting("screen.source", MeetVault.Core.CaptureSourceResolver.Format(_screenSources[ScreenSourceCombo.SelectedIndex]));
    }

    private void ScreenRefresh_Click(object sender, RoutedEventArgs e) => RefreshScreenSources();

    private void ScreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressScreenEvent || _screen is null) return;
        _screen.Enabled = ScreenToggle.IsOn;
        AppStore.Repo.SetSetting("screen.enabled", ScreenToggle.IsOn ? "true" : "false");
        RefreshScreenStatus();
    }

    private async void ScreenRun_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        try { await _screen.ToggleAsync(); PersistFinishedTracks(); }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: toggle failed: " + ex.Message); }
        RefreshScreenStatus();
    }

    private void PersistFinishedTracks()
    {
        if (_screen is null) return;
        foreach (var draft in _screen.FinishedTracks)
        {
            if (!draft.Ok) { Status(Loc.Tf("Live.ScreenFailed", draft.Error ?? "")); continue; }
            try
            {
                AppStore.Repo.AddMediaTrack(new MeetVault.Core.MediaTrack(
                    "trk-" + Guid.NewGuid().ToString("N"), draft.MeetingId, draft.Kind, draft.Path,
                    "video/mp4", draft.StartOffsetMs, draft.DurationMs, DateTimeOffset.UtcNow.ToString("o")));
                MeetVault.Core.AppLog.Info("screen: track saved offset=" + draft.StartOffsetMs + " ms -> " + draft.Path);
            }
            catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: track persist failed: " + ex.Message); }
        }
        _screen.ClearFinishedTracks();
    }

    private void RefreshScreenStatus()
    {
        if (_screen is null) return;
        var active = _screen.IsRecording;
        ScreenRunButton.Content = Loc.T(active ? "LiveScreenStop.Content" : "LiveScreenStart.Content");
        ScreenRunButton.IsEnabled = _state != LiveState.Idle;
        ScreenSourceCombo.IsEnabled = _state == LiveState.Idle;
        ScreenToggle.IsEnabled = _state == LiveState.Idle;
        ScreenStatus.Text = active ? Loc.Tf("Live.ScreenOn", SelectedSourceLabel())
            : _state == LiveState.Paused ? Loc.T("Live.ScreenPaused")
            : Loc.T("Live.ScreenOff");
        LiveBridge.SetScreenState(active);
        if (!active) { PreviewBox.Visibility = Visibility.Collapsed; PreviewImage.Source = null; }
    }

    private string SelectedSourceLabel()
        => ScreenSourceCombo.SelectedIndex >= 0 && ScreenSourceCombo.SelectedIndex < _screenSources.Count
            ? _screenSources[ScreenSourceCombo.SelectedIndex].Label : "";

    // Start-time source resolution: stored setting -> fresh enumerations -> primary display
    // with a logged reason (window HWNDs are session-local; this runs at every start).
    private MeetVault.Core.CaptureSource ResolveScreenSource()
    {
        var stored = AppStore.Repo.GetSetting("screen.source");
        var source = MeetVault.Core.CaptureSourceResolver.Resolve(
            stored, MeetVault.Core.CaptureSourceResolver.Displays(), MeetVault.Core.CaptureSourceResolver.Windows(), out var reason);
        if (reason.Length > 0) MeetVault.Core.AppLog.Info("screen: " + reason + "; using " + source.Label);
        return source;
    }

    // Preview frames arrive from the recorder's low-rate preview stream; render on the UI
    // thread. Cosmetic only — failures become log lines and never touch the recording.
    private void OnScreenFrame(byte[] data, int stride, int width, int height)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || data.Length < stride * (height - 1) + width * 4) return;
        var bytes = data;
        if (stride != width * 4)
        {
            var tight = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Array.Copy(bytes, y * stride, tight, y * width * 4, width * 4);
            bytes = tight;
        }
        var w = width;
        var h = height;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_screen?.IsRecording != true) return;
            try
            {
                var buffer = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(bytes);
                using var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                    buffer, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                _previewSink ??= new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                await _previewSink.SetBitmapAsync(bitmap);
                PreviewImage.Source = _previewSink;
                PreviewBox.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: preview frame failed: " + ex.Message); }
        });
    }

    // --- Language control ---

    // Single applier (manual pick, auto suggestion, resume): resolves the (language, size)
    // pair, swaps the relay's active engine, persists the meeting language and publishes
    // the state for the overlay.
    private void ApplyLanguage(string code, bool auto, string reason)
    {
        if (_relay is null) return;
        var size = AppStore.Repo.GetSetting("transcription.size") ?? "small";
        var option = EngineCatalog.Find(code, size) ?? EngineCatalog.Find(code, "small");
        if (option is null)
        {
            MeetVault.Core.AppLog.Warn("langid: " + code + " has no staged model; keeping " + _activeLanguage);
            SyncLanguageCombo(_autoMode ? "auto" : _activeLanguage);
            return;
        }
        // A MID-MEETING auto switch avoids the large tier (measured: large loads in ~16.5 s
        // vs 0.24 s small); the initial pick and manual picks keep the configured tier.
        if (reason == "auto" && size == "large" && _relay.ActiveModelDir is not null && code != _activeLanguage)
            option = EngineCatalog.Find(code, "small") ?? option;
        if (!_relay.SwapTo(Path.Combine(ModelDir, "vosk", option.Model)))
        {
            MeetVault.Core.AppLog.Warn("langid: swap failed for " + option.Model);
            return;
        }
        _activeLanguage = code;
        _engineMode = "live: vosk " + option.Model;
        // An auto-applied switch keeps the anti-thrash cooldown; a manual pick clears it so
        // the user's choice is never suppressed for the next cooldown window (review r6).
        if (auto) _langMonitor?.SetActiveKeepCooldown(code);
        else _langMonitor?.SetActive(code);
        if (_session?.MeetingId is { } id) AppStore.Repo.SetMeetingLanguage(id, code);
        LiveBridge.SetLanguage(new LiveBridge.LanguageInfo(code, _autoMode, auto));
        EngineStatus.Text = _engineMode;
        MeetVault.Core.AppLog.Info("live: language -> " + code + " (" + reason + ")");
        SetLangStatus();
    }

    private void OnLangSuggested(string code)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_autoMode || _relay is null) return;
            if (_relay.ActiveModelDir is null)
            {
                ApplyLanguage(code, auto: true, reason: "auto-initial");   // first detection decides the start
                return;
            }
            _pendingLanguage = code;
            _pendingSinceMs = Environment.TickCount64;
            Status(Loc.Tf("Live.LangSwitching", LiveBridge.LanguageDisplay(code)));
        });

    private void TryApplyPendingLanguage()
    {
        if (_pendingLanguage is null || _relay is null) return;
        var code = _pendingLanguage;
        _pendingLanguage = null;
        if (Environment.TickCount64 - _pendingSinceMs > 20_000)
            MeetVault.Core.AppLog.Warn("langid: switch applied with no endpoint for 20 s");
        ApplyLanguage(code, auto: true, reason: "auto");
    }

    private void OnLangDetected(string code, float confidence)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_autoMode) return;
            ToolTipService.SetToolTip(LangStatus, Loc.Tf("Live.LangDetected", LiveBridge.LanguageDisplay(code) + " " + confidence.ToString("P0")));
            // First detection while no engine is active decides the starting language.
            if (_relay is not null && _relay.ActiveModelDir is null && _state == LiveState.Recording)
                ApplyLanguage(code, auto: true, reason: "auto-initial");
        });

    private void OnLanguageRequested(string code)
    {
        var normalized = (code ?? "").Trim().ToLowerInvariant();
        if (normalized == "auto")
        {
            if (!MeetVault.Core.LangIdModel.IsAvailable(ModelDir)) Status(Loc.T("Set.LangIdMissing"));
            _autoMode = MeetVault.Core.LangIdModel.IsAvailable(ModelDir);
            EnsureLangMonitor();
            if (_langMonitor is not null) _langMonitor.Enabled = true;
            PersistLanguage("auto");
            LiveBridge.SetLanguage(new LiveBridge.LanguageInfo(_activeLanguage, _autoMode, false));
            SyncLanguageCombo("auto");
            SetLangStatus();
            return;
        }
        _autoMode = false;
        if (_langMonitor is not null) _langMonitor.Enabled = false;
        PersistLanguage(normalized);
        if (_relay is not null) ApplyLanguage(normalized, auto: false, reason: "manual");
        SyncLanguageCombo(normalized);
        SetLangStatus();
    }

    private static void PersistLanguage(string code) => AppStore.Repo.SetSetting("transcription.language", code);

    // Overlay screen button (F): same controller as the Live section; the state goes back
    // out through LiveBridge.SetScreenState (via RefreshScreenStatus) — never set directly.
    private void OnScreenToggleRequested() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_screen is null) return;
        if (_session is null || _state == LiveState.Idle)
        {
            Status(Loc.T("Ov.NotRecording"));
            return;
        }
        try { await _screen.ToggleAsync(); PersistFinishedTracks(); }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: overlay toggle failed: " + ex.Message); }
        RefreshScreenStatus();
    });

    private void BuildLanguageCombo(string current)
    {
        _suppressLanguageEvent = true;
        try
        {
            LanguageCombo.Items.Clear();
            LanguageCombo.Items.Add(Loc.T("Lang.Auto"));
            foreach (var l in EngineCatalog.Languages) LanguageCombo.Items.Add(l.Endonym);
            LanguageCombo.SelectedIndex = current == "auto" ? 0
                : Math.Max(0, Array.FindIndex(EngineCatalog.Languages, l => l.Code == current) + 1);
        }
        finally { _suppressLanguageEvent = false; }
        SetLangStatus();
    }

    private void SyncLanguageCombo(string code) => BuildLanguageCombo(code);

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent) return;
        var idx = LanguageCombo.SelectedIndex;
        if (idx < 0) return;
        LiveBridge.RequestLanguage(idx == 0 ? "auto" : EngineCatalog.Languages[idx - 1].Code);
    }

    private void SetLangStatus()
    {
        if (LangStatus is null) return;
        var mode = _autoMode ? Loc.T("Live.LangModeAuto") : Loc.T("Live.LangModeManual");
        LangStatus.Text = LiveBridge.LanguageDisplay(_activeLanguage) + " · " + mode;
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
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Idle);
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
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Paused);
            _langMonitor?.Reset();
            if (_screen is not null)
            {
                try { await _screen.MeetingPausedAsync(); PersistFinishedTracks(); }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: pause hook failed: " + ex.Message); }
            }
            RefreshScreenStatus();
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
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Recording);
            if (_screen is not null)
            {
                try { await _screen.MeetingResumedAsync(); PersistFinishedTracks(); }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: resume hook failed: " + ex.Message); }
            }
            RefreshScreenStatus();
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
            LiveBridge.SetMeetingState(LiveBridge.MeetingState.Idle);
            _langMonitor?.Reset();
            _pendingLanguage = null;
            _checkpoint?.Stop();
            SetState(LiveState.Idle);
            if (_screen is not null)
            {
                // Finalize the screen run(s) — never let a recorder error block the Stop path.
                try { await _screen.MeetingStoppedAsync(); PersistFinishedTracks(); }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("screen: stop hook failed: " + ex.Message); }
            }
            RefreshScreenStatus();
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
