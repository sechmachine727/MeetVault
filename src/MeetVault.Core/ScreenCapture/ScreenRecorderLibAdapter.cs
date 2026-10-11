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

    // One native recorder per run, disposed exactly once (review r10): the C++/CLI wrapper
    // must not be freed twice (a synchronous failure event plus a Record throw can both try)
    // and never from inside its own completion callback.
    private sealed class RunRecorder
    {
        private int _disposed;
        public Recorder Recorder { get; }
        public RunRecorder(Recorder recorder) { Recorder = recorder; }
        public void DisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Recorder.Dispose(); } catch { }
        }
    }
    private string? _outputPath;
    private TaskCompletionSource<RecordingResult>? _done;
    private long _startTicks;

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
            // must not leak a recorder or leave _done set (review r8).
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var recorder = Create(source, OnFrameRecorded);
            _outputPath = outputPath;
            _done = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.OnRecordingComplete += (_, e) => Finish(true, e.FilePath, null);
            recorder.OnRecordingFailed += (_, e) => Finish(false, e.FilePath, e.Error);
            _startTicks = Stopwatch.GetTimestamp();
            // Publish the recorder before Record: a throw from Record (e.g. the missing-CRT
            // path) must not leak the native recorder or leave _done dangling (review r7).
            var run = new RunRecorder(recorder);
            _recorder = run;
            IsRecording = true;
            try { recorder.Record(outputPath); }
            catch
            {
                _recorder = null;
                IsRecording = false;
                _done = null;
                run.DisposeOnce(); // idempotent with a synchronous failure event (review r10)
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
    {
        RunRecorder? recorder;
        TaskCompletionSource<RecordingResult>? done;
        string? path;
        bool notRecording;
        lock (_gate)
        {
            recorder = _recorder;
            done = _done;
            path = _outputPath;
            notRecording = recorder is null || done is null || !IsRecording;
        }
        if (notRecording)
        {
            // Already finalized (mid-run failure) or never started.
            if (done is not null && done.Task.IsCompleted) return await done.Task.ConfigureAwait(false);
            return new RecordingResult(false, path, 0, "not recording");
        }
        try { recorder!.Recorder.Stop(); }
        catch (Exception ex)
        {
            var failed = EndRun();
            failed?.DisposeOnce();
            return new RecordingResult(false, path, ElapsedMs(), ex.Message);
        }
        var winner = await Task.WhenAny(done!.Task, Task.Delay(3000, ct)).ConfigureAwait(false);
        if (winner == done.Task) return await done.Task.ConfigureAwait(false);
        // Timed out: the finalize may still complete (a long MP4's moov/faststart rewrite
        // can outlast 3 s). The native recorder is released, and the result is marked
        // Indeterminate so the caller KEEPS the file instead of deleting it (review r13).
        var abandoned = EndRun();
        abandoned?.DisposeOnce();
        return new RecordingResult(false, path, ElapsedMs(), "stop timed out", Indeterminate: true);
    }

    public void Dispose()
    {
        RunRecorder? rec;
        lock (_gate) { rec = _recorder; _recorder = null; IsRecording = false; }
        try { rec?.Recorder.Stop(); } catch { }
        rec?.DisposeOnce(); // release the native recorder exactly once (review r10)
    }

    private void Finish(bool ok, string? path, string? error)
    {
        TaskCompletionSource<RecordingResult>? done;
        RunRecorder? rec;
        lock (_gate)
        {
            done = _done;
            if (done is null || done.Task.IsCompleted) return; // late duplicate event
            IsRecording = false;
            rec = _recorder;
            _recorder = null;
            done.TrySetResult(new RecordingResult(ok, path ?? _outputPath, ElapsedMs(), error));
        }
        if (!ok && error is not null) Failed?.Invoke(error);
        // Hand the dispose off this callback thread: freeing the wrapper inside its own
        // completion event is unsafe (review r10). DisposeOnce keeps it exactly-once.
        if (rec is not null) _ = Task.Run(rec.DisposeOnce);
    }

    private RunRecorder? EndRun()
    {
        RunRecorder? rec;
        lock (_gate) { rec = _recorder; IsRecording = false; _recorder = null; }
        return rec;
    }

    private long ElapsedMs() => _startTicks == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

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
