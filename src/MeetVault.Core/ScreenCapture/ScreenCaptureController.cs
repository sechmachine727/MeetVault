namespace MeetVault.Core;

/// <summary>One finished screen-recording run. The app maps each draft to a
/// media_tracks row (kind "screen", mime "video/mp4"). Indeterminate: finalize may still
/// be running — the caller keeps the file and still writes the row (review r13).</summary>
public sealed record MediaTrackDraft(string MeetingId, string Kind, string Path, long StartOffsetMs, long DurationMs, bool Ok, string? Error, bool Indeterminate = false);

/// <summary>Decides when screen-video runs start and stop (meeting state × enabled × user
/// toggle), builds run output paths, computes per-run timeline offsets, and finalizes
/// drafts. One MP4 per run: Pause finalizes the run; Resume starts a new one. A recorder
/// failure mid-run finalizes the track as failed and never throws. Windows are watched
/// for liveness (probe P2 #3: a destroyed capture window raises NO recorder event).</summary>
public sealed class ScreenCaptureController : IDisposable
{
    private enum MeetingPhase { Idle, Recording, Paused }
    private sealed record Run(string Path, long StartOffsetMs, long StartWallMs);

    private readonly IVideoRecorder _recorder;
    private readonly Func<string?> _mediaDir;
    private readonly Func<long> _clock;
    private readonly Func<CaptureSource>? _resolveSource;
    private readonly Func<CaptureSource, IntPtr>? _windowHandle;
    private readonly Func<IntPtr, bool>? _isWindowAlive;
    private readonly int _watchdogPollMs;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly List<MediaTrackDraft> _drafts = new();
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);
    private MeetingPhase _phase = MeetingPhase.Idle;
    private string? _meetingId;
    private long _meetingStartMs;
    private long _pausedTotalMs;
    private long _pauseStartMs;
    private Run? _run;
    private CancellationTokenSource? _watchdog;

    /// <summary>Screen recording is on by default for new meetings (screen.enabled).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True while a run is active (started and not yet finalized).</summary>
    public bool IsRecording => _run is not null;

    /// <summary>Non-fatal problems: couldn't start, run failed, target window closed.</summary>
    public event Action<string>? Warning;

    /// <summary>Low-rate screen preview frames (BGRA), forwarded from the recorder.</summary>
    public event Action<byte[], int, int, int>? FramePreview;

    public IReadOnlyList<MediaTrackDraft> FinishedTracks { get { lock (_drafts) return _drafts.ToArray(); } }

    public ScreenCaptureController(IVideoRecorder recorder, Func<string?> mediaDir, Func<long> clock,
        Func<CaptureSource>? resolveSource = null,
        Func<CaptureSource, IntPtr>? windowHandle = null,
        Func<IntPtr, bool>? isWindowAlive = null,
        int watchdogPollMs = 1000)
    {
        _recorder = recorder;
        _mediaDir = mediaDir;
        _clock = clock;
        _resolveSource = resolveSource;
        _windowHandle = windowHandle;
        _isWindowAlive = isWindowAlive;
        _watchdogPollMs = watchdogPollMs;
        _recorder.Failed += OnRecorderFailed;
        _recorder.FramePreview += OnFramePreview;
    }

    public async Task MeetingStartedAsync(string meetingId, long nowMs, bool paused = false)
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            _phase = paused ? MeetingPhase.Paused : MeetingPhase.Recording;
            _meetingId = meetingId;
            _meetingStartMs = nowMs;
            _pausedTotalMs = 0;
            _pauseStartMs = paused ? _clock() : 0;
            // A recovered draft that is still paused must not open a run; the resume does
            // (review r6).
            if (!paused) await StartRunAsync("meeting start").ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    public async Task MeetingPausedAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_phase != MeetingPhase.Recording) return;
            _phase = MeetingPhase.Paused;
            _pauseStartMs = _clock();
            await StopRunAsync("meeting paused").ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    public async Task MeetingResumedAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_phase != MeetingPhase.Paused) return;
            _phase = MeetingPhase.Recording;
            // The recorded-audio timeline excludes paused time; remember how much was
            // paused so later run offsets stay on that timeline (review r5).
            _pausedTotalMs += Math.Max(0, _clock() - _pauseStartMs);
            await StartRunAsync("meeting resumed").ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    public async Task MeetingStoppedAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            _phase = MeetingPhase.Idle;
            await StopRunAsync("meeting stopped").ConfigureAwait(false);
            _meetingId = null;
        }
        finally { _one.Release(); }
    }

    /// <summary>The overlay/live toggle: stop the active run, or start one while recording.</summary>
    public async Task ToggleAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_run is not null) { await StopRunAsync("toggled off").ConfigureAwait(false); return; }
            if (_phase != MeetingPhase.Recording) { Warn("screen: toggle ignored - the meeting is not recording"); return; }
            await StartRunAsync("toggled on").ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    public void ClearFinishedTracks()
    {
        lock (_drafts) _drafts.Clear();
    }

    public void Dispose()
    {
        _recorder.Failed -= OnRecorderFailed;
        _recorder.FramePreview -= OnFramePreview;
        StopWatchdog();
    }

    private void OnFramePreview(byte[] data, int stride, int width, int height) => FramePreview?.Invoke(data, stride, width, height);

    private async Task StartRunAsync(string reason)
    {
        if (_run is not null || _phase != MeetingPhase.Recording) return;
        if (!Enabled) return;
        var dir = _mediaDir();
        if (string.IsNullOrEmpty(dir) || _meetingId is null)
        {
            Warn("screen: no media directory; screen run not started");
            return;
        }
        var source = _resolveSource?.Invoke();
        if (source is null)
        {
            Warn("screen: no capture source; screen run not started");
            return;
        }
        var now = _clock();
        var path = RunPath(dir!, _meetingId!, now);
        // Offset on the RECORDED-audio timeline (wall elapsed minus paused time): the WAV
        // excludes pauses, and the WAV is what the video must align to (review r5).
        var run = new Run(path, now - _meetingStartMs - _pausedTotalMs, now);
        try
        {
            await _recorder.StartAsync(source, path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn("screen: could not start recording (" + ex.Message + ")");
            return;
        }
        // The adapter can fail synchronously (a failure event during Record clears its
        // state before returning): don't claim a run that captured nothing (review r13).
        if (!_recorder.IsRecording)
        {
            Warn("screen: recorder failed during start");
            return;
        }
        _run = run;
        AppLog.Info("screen: run started -> " + path + " (offset " + run.StartOffsetMs + " ms; " + reason + ")");
        StartWatchdog(source);
    }

    private async Task StopRunAsync(string reason)
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        StopWatchdog();
        RecordingResult result;
        try { result = await _recorder.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { result = new RecordingResult(false, run.Path, _clock() - run.StartWallMs, ex.Message); }
        var duration = result.DurationMs > 0 ? result.DurationMs : Math.Max(0, _clock() - run.StartWallMs);
        var draft = new MediaTrackDraft(_meetingId ?? "", "screen", result.Path ?? run.Path, run.StartOffsetMs, duration, result.Ok, result.Error, result.Indeterminate);
        lock (_drafts) _drafts.Add(draft);
        if (!result.Ok) Warn("screen: run failed (" + (result.Error ?? "unknown") + ")");
        AppLog.Info("screen: run stopped (" + reason + ") ok=" + result.Ok + " -> " + draft.Path);
    }

    private void OnRecorderFailed(string error)
    {
        AppLog.Warn("screen: recorder failure: " + error);
        if (_run is not null) _ = StopViaGate("recorder failure");
    }

    // Recorder failures and the watchdog fire outside the entry gate; routing the stop
    // through it keeps StopRunAsync's _run read-and-clear atomic against a racing user
    // pause/stop, which could otherwise finalize one run twice and append two tracks for
    // one file (review r6).
    private async Task StopViaGate(string reason)
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try { await StopRunAsync(reason).ConfigureAwait(false); }
        finally { _one.Release(); }
    }

    private void StartWatchdog(CaptureSource source)
    {
        if (source.Kind != "window" || _windowHandle is null || _isWindowAlive is null) return;
        var hwnd = _windowHandle(source);
        if (hwnd == IntPtr.Zero) return; // the adapter already fell back to the display
        StopWatchdog();
        var cts = new CancellationTokenSource();
        _watchdog = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var watched = _run;
                    if (watched is null) return;
                    await Task.Delay(_watchdogPollMs, token).ConfigureAwait(false);
                    // Act only on the run this iteration observed (review r10): a pause/
                    // resume retires it, and its replacement must not be stopped by us.
                    if (token.IsCancellationRequested || !ReferenceEquals(_run, watched)) continue;
                    if (_isWindowAlive(hwnd)) continue;
                    Warn("screen: target window closed; stopping the screen run");
                    await StopViaGate("window closed").ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("screen: watchdog failed: " + ex.Message); }
        });
    }

    private void StopWatchdog()
    {
        try { _watchdog?.Cancel(); } catch { }
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private void Warn(string message)
    {
        AppLog.Warn(message);
        Warning?.Invoke(message);
    }

    /// <summary>&lt;mediaDir&gt;/&lt;meetingId&gt;-screen-&lt;utc yyyyMMddHHmmss&gt;.mp4; a same-second
    /// collision (pause/resume) gets a numeric suffix so runs never share a file.</summary>
    private string RunPath(string mediaDir, string meetingId, long wallMs)
    {
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(wallMs).UtcDateTime.ToString("yyyyMMddHHmmss");
        var basePath = Path.Combine(mediaDir, meetingId + "-screen-" + stamp);
        var path = basePath + ".mp4";
        for (var n = 2; _usedPaths.Contains(path) || File.Exists(path); n++) path = basePath + "-" + n + ".mp4";
        _usedPaths.Add(path);
        return path;
    }
}
