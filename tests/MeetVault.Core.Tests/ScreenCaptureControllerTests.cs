using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class ScreenCaptureControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mv-screen-tests-" + Guid.NewGuid().ToString("N"));

    public ScreenCaptureControllerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeRecorder : IVideoRecorder
    {
        public bool IsRecording { get; private set; }
        public bool Overlapped { get; private set; }
        public bool FailStops { get; set; }
        public List<(CaptureSource Source, string Path)> Starts { get; } = new();
        public event Action<string>? Failed;
        public event Action<bool>? StateChanged;

        public Task StartAsync(CaptureSource source, string outputPath, CancellationToken ct = default)
        {
            if (IsRecording) Overlapped = true;
            IsRecording = true;
            Starts.Add((source, outputPath));
            StateChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task<RecordingResult> StopAsync(CancellationToken ct = default)
        {
            IsRecording = false;
            StateChanged?.Invoke(false);
            var path = Starts.Count > 0 ? Starts[^1].Path : null;
            return Task.FromResult(FailStops
                ? new RecordingResult(false, path, 4000, "encoder boom")
                : new RecordingResult(true, path, 4000, null));
        }

        public void RaiseFailed(string error)
        {
            IsRecording = false;
            Failed?.Invoke(error);
        }

        public void Dispose() { }
    }

    private (ScreenCaptureController Controller, FakeRecorder Recorder, List<string> Warnings) Make(
        long[] clockValues, Func<CaptureSource, IntPtr>? windowHandle = null,
        Func<IntPtr, bool>? isWindowAlive = null, int pollMs = 10)
    {
        var recorder = new FakeRecorder();
        var warnings = new List<string>();
        var i = 0;
        var source = new CaptureSource("display", "", @"\\.\DISPLAY1 (2560x1440)");
        var controller = new ScreenCaptureController(recorder, () => _dir, () => clockValues[Math.Min(i++, clockValues.Length - 1)],
            () => source, windowHandle, isWindowAlive, pollMs);
        controller.Warning += warnings.Add;
        return (controller, recorder, warnings);
    }

    [Fact]
    public async Task StartStop_OneDraft_WithCorrectOffsetAndPath()
    {
        // Clock: meeting starts at 1_000_000; the run starts at 1_004_000 -> offset 4000.
        var (c, rec, _) = Make(new long[] { 1_004_000 });
        await c.MeetingStartedAsync("m1", 1_000_000);
        await c.MeetingStoppedAsync();

        Assert.Single(rec.Starts);
        Assert.False(rec.Overlapped);
        var expected = Path.Combine(_dir, "m1-screen-" + DateTimeOffset.FromUnixTimeMilliseconds(1_004_000).UtcDateTime.ToString("yyyyMMddHHmmss") + ".mp4");
        Assert.Equal(expected, rec.Starts[0].Path);
        var draft = Assert.Single(c.FinishedTracks);
        Assert.Equal(4000, draft.StartOffsetMs);
        Assert.Equal("m1", draft.MeetingId);
        Assert.Equal("screen", draft.Kind);
        Assert.True(draft.Ok);
        Assert.Equal(4000, draft.DurationMs);
    }

    [Fact]
    public async Task Pause_Finalizes_Resume_StartsSecondFile()
    {
        var (c, rec, _) = Make(new long[] { 1_000_100, 1_000_200, 1_000_300 });
        await c.MeetingStartedAsync("m1", 1_000_000);
        await c.MeetingPausedAsync();
        var first = Assert.Single(c.FinishedTracks);
        await c.MeetingResumedAsync();
        await c.MeetingStoppedAsync();

        Assert.Equal(2, rec.Starts.Count);
        Assert.Equal(2, c.FinishedTracks.Count);
        Assert.NotEqual(rec.Starts[0].Path, rec.Starts[1].Path); // same second: collision suffix
        Assert.True(first.Ok);
    }

    [Fact]
    public async Task ToggleTwice_NeverOverlapsRuns()
    {
        var (c, rec, _) = Make(new long[] { 1_000_100, 1_000_200, 1_000_300 });
        await c.MeetingStartedAsync("m1", 1_000_000); // auto run starts with the meeting
        await Task.WhenAll(c.ToggleAsync(), c.ToggleAsync()); // stop it, then toggle back on: serialized
        await c.MeetingStoppedAsync();

        Assert.False(rec.Overlapped);
        Assert.Equal(2, rec.Starts.Count);
        Assert.Equal(2, c.FinishedTracks.Count);
    }

    [Fact]
    public async Task FailedMidRun_FinalizesFailedDraftAndWarns()
    {
        var (c, rec, warnings) = Make(new long[] { 1_000_100, 1_000_500 });
        await c.MeetingStartedAsync("m1", 1_000_000);
        rec.FailStops = true;
        rec.RaiseFailed("encoder boom");
        await WaitUntil(() => c.FinishedTracks.Count == 1);

        var draft = Assert.Single(c.FinishedTracks);
        Assert.False(draft.Ok);
        Assert.Equal("encoder boom", draft.Error);
        Assert.Contains(warnings, w => w.Contains("run failed"));
        await c.MeetingStoppedAsync(); // must not double-finalize
        Assert.Single(c.FinishedTracks);
    }

    [Fact]
    public async Task Disabled_NeverStarts()
    {
        var (c, rec, _) = Make(new long[] { 1_000_100 });
        c.Enabled = false;
        await c.MeetingStartedAsync("m1", 1_000_000);
        await c.ToggleAsync();
        Assert.Empty(rec.Starts);
        Assert.Empty(c.FinishedTracks);
    }

    [Fact]
    public async Task WindowClosed_WatchdogStopsTheRun()
    {
        var alive = true;
        var hwnd = new IntPtr(0x1234);
        var source = new CaptureSource("window", "notepad", "notepad — Untitled");
        var recorder = new FakeRecorder();
        var warnings = new List<string>();
        var i = 0;
        var clock = new long[] { 1_000_100, 1_000_600 };
        var c = new ScreenCaptureController(recorder, () => _dir, () => clock[Math.Min(i++, clock.Length - 1)],
            () => source, _ => hwnd, _ => alive, 10);
        c.Warning += warnings.Add;

        await c.MeetingStartedAsync("m1", 1_000_000);
        alive = false;
        await WaitUntil(() => c.FinishedTracks.Count == 1);
        Assert.Contains(warnings, w => w.Contains("window closed"));
        Assert.False(recorder.IsRecording);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
        Assert.True(condition(), "condition not met within " + timeoutMs + " ms");
    }
}
