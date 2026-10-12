using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace MeetVault.Core;

/// <summary>The only production file that references ScreenRecorderLib (v7.0.1, MIT).
/// Display capture uses WindowsGraphicsCapture: the DesktopDuplication default renders
/// black frames on this box (virtual display adapter topology — probe P2). Window targets
/// resolve a live HWND at start; a window that is gone falls back to the primary display.
/// Video-only: audio stays in NAudioCapture; playback pairs the MP4 with the m4a.</summary>
public sealed class ScreenRecorderLibAdapter : IVideoRecorder
{
    private readonly object _gate = new();
    private RunRecorder? _recorder;

    // One native recorder per run: its OWN completion source (a late event from an
    // abandoned run can never complete another run's stop — review r14) and an
    // exactly-once dispose (a synchronous failure event plus a Record throw can both try —
    // review r10).
    private sealed class RunRecorder
    {
        private int _disposed;
        public Recorder Recorder { get; }
        public string OutputPath { get; }
        public long StartTicks { get; }
        public TaskCompletionSource<RecordingResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<RecordingCompleteEventArgs>? CompleteHandler { get; set; }
        public EventHandler<RecordingFailedEventArgs>? FailedHandler { get; set; }
        public RunRecorder(Recorder recorder, string outputPath)
        {
            Recorder = recorder;
            OutputPath = outputPath;
            StartTicks = Stopwatch.GetTimestamp();
        }
        public long ElapsedMs() => StartTicks == 0 ? 0 : (long)Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;
        public void DisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Recorder.Dispose(); } catch { }
        }
    }

    // While Record() is on the stack a failure event can fire synchronously during it, so a
    // finished run must be retired — disposed only after Record returns — never from the
    // pool, which would free the native wrapper under the in-flight call (review r14).
    private bool _recordInProgress;
    private RunRecorder? _retireAfterRecord;

    public bool IsRecording { get; private set; }
    public event Action<string>? Failed;
    public event Action<byte[], int, int, int>? FramePreview;

    public Task StartAsync(CaptureSource source, string outputPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("already recording");
            // Before the native recorder exists: a bad/long path or permission failure here
            // must not leak a recorder or leave state set (review r8).
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var recorder = Create(source, OnFrameRecorded);
            var run = new RunRecorder(recorder, outputPath);
            run.CompleteHandler = (_, e) => Finish(run, true, e.FilePath, null);
            run.FailedHandler = (_, e) => Finish(run, false, e.FilePath, e.Error);
            recorder.OnRecordingComplete += run.CompleteHandler;
            recorder.OnRecordingFailed += run.FailedHandler;
            // Publish the run before Record: a throw from Record (e.g. the missing-CRT
            // path) must not leak the native recorder (review r7).
            _recorder = run;
            IsRecording = true;
            _recordInProgress = true;
            try { recorder.Record(outputPath); }
            catch
            {
                _recorder = null;
                IsRecording = false;
                run.DisposeOnce(); // idempotent with a synchronous failure event (review r10)
                throw;
            }
            finally
            {
                // A run that finished during Record is retired only now, when Record has
                // returned (review r14).
                RunRecorder? pending;
                lock (_gate) { _recordInProgress = false; pending = _retireAfterRecord; _retireAfterRecord = null; }
                pending?.DisposeOnce();
            }
        }
        return Task.CompletedTask;
    }

    public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
    {
        RunRecorder? recorder;
        lock (_gate) recorder = _recorder;
        if (recorder is null || !IsRecording)
        {
            // Already finalized (mid-run failure) or never started.
            if (recorder is not null && recorder.Done.Task.IsCompleted) return await recorder.Done.Task.ConfigureAwait(false);
            return new RecordingResult(false, recorder?.OutputPath, 0, "not recording");
        }
        // Stop() is a synchronous native call that can block; never let it hold the
        // controller's operation gate forever — run it on the pool and race the completion
        // event against the same 3 s budget (review r20).
        var stopTask = Task.Run(() => { try { recorder.Recorder.Stop(); } catch { } });
        var winner = await Task.WhenAny(recorder.Done.Task, Task.Delay(3000, ct)).ConfigureAwait(false);
        if (winner == recorder.Done.Task) return await recorder.Done.Task.ConfigureAwait(false);
        // Timed out: the native Stop may still be in flight — disposing under it can fault
        // natively (uncatchable). Hand the run to the stop task's continuation instead
        // (review r21).
        var abandoned = EndRun();
        if (abandoned is not null) _ = stopTask.ContinueWith(_ => abandoned.DisposeOnce(), TaskScheduler.Default);
        return new RecordingResult(false, recorder.OutputPath, recorder.ElapsedMs(), "stop timed out", Indeterminate: true);
    }

    public void Dispose()
    {
        RunRecorder? rec;
        lock (_gate) { rec = _recorder; _recorder = null; IsRecording = false; }
        if (rec is not null)
        {
            // Never Stop() a run whose Finish already handed it to the pool: Stop() on a
            // disposed C++/CLI recorder can fault natively (uncatchable), and the success
            // path of StopAsync deliberately leaves _recorder set (review r19).
            if (!rec.Done.Task.IsCompleted) { try { rec.Recorder.Stop(); } catch { } }
            rec.DisposeOnce(); // release the native recorder exactly once (review r10)
        }
    }

    private void Finish(RunRecorder run, bool ok, string? path, string? error)
    {
        if (!run.Done.TrySetResult(new RecordingResult(ok, path ?? run.OutputPath, run.ElapsedMs(), error))) return; // late duplicate event
        lock (_gate) { if (ReferenceEquals(_recorder, run)) IsRecording = false; }
        if (!ok && error is not null) Failed?.Invoke(error);
        // Never free the wrapper from the pool while Record() is on the stack (a failure
        // event fires synchronously during Record): retire it after Record returns
        // (review r14). Otherwise hand the dispose off this callback thread (review r10).
        var retire = false;
        lock (_gate)
        {
            if (_recordInProgress) { _retireAfterRecord = run; retire = true; }
        }
        if (!retire) _ = Task.Run(run.DisposeOnce);
    }

    private RunRecorder? EndRun()
    {
        RunRecorder? rec;
        lock (_gate) { rec = _recorder; IsRecording = false; _recorder = null; }
        if (rec is not null)
        {
            // Detach so a late completion/failure from the abandoned run can never reach
            // any live state (review r14).
            try { rec.Recorder.OnRecordingComplete -= rec.CompleteHandler; } catch { }
            try { rec.Recorder.OnRecordingFailed -= rec.FailedHandler; } catch { }
        }
        return rec;
    }

    private static Recorder Create(CaptureSource source, EventHandler<FrameRecordedEventArgs> frameHandler)
    {
        RecordingSourceBase src;
        if (source.Kind == "window")
        {
            var (exe, titlePrefix) = ParseWindow(source);
            var hwnd = FindWindowHandle(exe, titlePrefix);
            if (hwnd == IntPtr.Zero)
            {
                AppLog.Warn("screen: target window '" + source.Label + "' is gone; recording the primary display instead");
                src = DisplayFor("");
            }
            else
            {
                src = new WindowRecordingSource(hwnd) { IsBorderRequired = false, IsCursorCaptureEnabled = false };
            }
        }
        else
        {
            src = DisplayFor(source.Id);
        }

        RecorderOptions opts;
        try { opts = RecorderOptions.Default; }
        catch (Exception ex) { throw new InvalidOperationException("screen capture unavailable: " + ex.Message, ex); }
        // Low-rate preview frames for the live preview box (320x180, library-throttled).
        src.IsVideoFramePreviewEnabled = true;
        src.VideoFramePreviewSize = new ScreenSize(320, 180);
        opts.SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { src } };
        opts.AudioOptions = new AudioOptions { IsAudioEnabled = false }; // video-only; audio stays in NAudioCapture
        opts.VideoEncoderOptions = new VideoEncoderOptions { Framerate = 30, Bitrate = 4_000_000, Quality = 70 };
        try
        {
            var recorder = Recorder.CreateRecorder(opts);
            recorder.OnFrameRecorded += frameHandler;
            return recorder;
        }
        catch (Exception ex) // DllNotFound/TypeInit/BadImageFormat when the VC++ x64 redist is missing
        {
            throw new InvalidOperationException("screen capture unavailable: " + ex.Message, ex);
        }
    }

    private void OnFrameRecorded(object? sender, FrameRecordedEventArgs e)
    {
        var b = e.BitmapData;
        if (b is null || b.Data == IntPtr.Zero || b.Length <= 0) return;
        try
        {
            var bytes = new byte[b.Length];
            Marshal.Copy(b.Data, bytes, 0, b.Length);
            FramePreview?.Invoke(bytes, b.Stride, b.Width, b.Height);
        }
        catch (Exception ex) { AppLog.Warn("screen: preview frame copy failed: " + ex.Message); }
    }

    /// <summary>Display source for a device name ("" = primary). WGC is mandatory here:
    /// DesktopDuplication produced solid-black frames on this box (probe P2).</summary>
    private static RecordingSourceBase DisplayFor(string deviceName)
    {
        var src = string.IsNullOrEmpty(deviceName) ? DisplayRecordingSource.MainMonitor : new DisplayRecordingSource(deviceName);
        if (src is null) throw new InvalidOperationException("no display available for capture");
        src.RecorderApi = RecorderApi.WindowsGraphicsCapture;
        return src;
    }

    /// <summary>A stored window source: Id = lowercase exe, Label = "exe — title" (the
    /// resolver formats both). The title may have changed since storage — prefix-match
    /// either way; a missing window degrades to the primary display at start.</summary>
    private static (string Exe, string TitlePrefix) ParseWindow(CaptureSource source)
    {
        var title = "";
        var sep = source.Label.IndexOf(" — ", StringComparison.Ordinal);
        if (sep >= 0 && sep + 3 <= source.Label.Length) title = source.Label[(sep + 3)..];
        return (source.Id, title);
    }

    private static IntPtr FindWindowHandle(string exe, string titlePrefix)
    {
        try
        {
            foreach (var w in Recorder.GetWindows())
            {
                if (w is null) continue;
                var title = w.Title ?? "";
                if (title.Length == 0) continue;
                if (exe.Length > 0 && !string.Equals(ExeName(w.Pid), exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (titlePrefix.Length > 0 &&
                    !title.StartsWith(titlePrefix, StringComparison.Ordinal) &&
                    !titlePrefix.StartsWith(title, StringComparison.Ordinal))
                    continue;
                return w.Handle;
            }
        }
        catch (Exception ex) { AppLog.Warn("screen: window enumeration failed: " + ex.Message); }
        return IntPtr.Zero;
    }

    private static string ExeName(int? pid)
    {
        try
        {
            if (pid is null or <= 0) return "";
            using var p = Process.GetProcessById(pid.Value); // the handle must not leak per window (review r10)
            return p.ProcessName;
        }
        catch { return ""; }
    }
}
