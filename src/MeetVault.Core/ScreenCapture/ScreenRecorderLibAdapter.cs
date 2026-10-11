using System.Diagnostics;
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
    private Recorder? _recorder;
    private string? _outputPath;
    private TaskCompletionSource<RecordingResult>? _done;
    private long _startTicks;

    public bool IsRecording { get; private set; }
    public event Action<string>? Failed;
    public event Action<bool>? StateChanged;

    public Task StartAsync(CaptureSource source, string outputPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("already recording");
            var recorder = Create(source);
            _outputPath = outputPath;
            _done = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.OnRecordingComplete += (_, e) => Finish(true, e.FilePath, null);
            recorder.OnRecordingFailed += (_, e) => Finish(false, e.FilePath, e.Error);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            _startTicks = Stopwatch.GetTimestamp();
            recorder.Record(outputPath);
            _recorder = recorder;
            IsRecording = true;
        }
        StateChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
    {
        Recorder? recorder;
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
        try { recorder!.Stop(); }
        catch (Exception ex)
        {
            EndRun();
            return new RecordingResult(false, path, ElapsedMs(), ex.Message);
        }
        var winner = await Task.WhenAny(done!.Task, Task.Delay(3000, ct)).ConfigureAwait(false);
        if (winner == done.Task) return await done.Task.ConfigureAwait(false);
        EndRun();
        return new RecordingResult(false, path, ElapsedMs(), "stop timed out");
    }

    public void Dispose()
    {
        Recorder? rec;
        lock (_gate) { rec = _recorder; _recorder = null; IsRecording = false; }
        try { rec?.Stop(); } catch { }
    }

    private void Finish(bool ok, string? path, string? error)
    {
        TaskCompletionSource<RecordingResult>? done;
        lock (_gate)
        {
            done = _done;
            if (done is null || done.Task.IsCompleted) return; // late duplicate event
            IsRecording = false;
            _recorder = null;
            done.TrySetResult(new RecordingResult(ok, path ?? _outputPath, ElapsedMs(), error));
        }
        StateChanged?.Invoke(false);
        if (!ok && error is not null) Failed?.Invoke(error);
    }

    private void EndRun()
    {
        lock (_gate) { IsRecording = false; _recorder = null; }
        StateChanged?.Invoke(false);
    }

    private long ElapsedMs() => _startTicks == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    private static Recorder Create(CaptureSource source)
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
        opts.SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { src } };
        opts.AudioOptions = new AudioOptions { IsAudioEnabled = false }; // video-only; audio stays in NAudioCapture
        opts.VideoEncoderOptions = new VideoEncoderOptions { Framerate = 30, Bitrate = 4_000_000, Quality = 70 };
        try
        {
            return Recorder.CreateRecorder(opts);
        }
        catch (Exception ex) // DllNotFound/TypeInit/BadImageFormat when the VC++ x64 redist is missing
        {
            throw new InvalidOperationException("screen capture unavailable: " + ex.Message, ex);
        }
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
        try { return pid is null or <= 0 ? "" : Process.GetProcessById(pid.Value).ProcessName; }
        catch { return ""; }
    }
}
