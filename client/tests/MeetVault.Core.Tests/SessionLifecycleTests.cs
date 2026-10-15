using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class SessionLifecycleTests
{
    [Fact]
    public async Task DoubleStop_Throws_NoDoubleSave()
    {
        var store = new FakeStore();
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), store);
        await session.StartAsync("mic", "app");
        await session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
    }

    [Fact]
    public async Task Dispose_Unsubscribes_NoFurtherSegments()
    {
        var tx = new RawTranscriber();
        var session = new MeetingSession(new FakeAudio(), tx, new FakeStore());
        await session.StartAsync("mic", "app");
        session.Dispose();
        tx.Emit(new Segment("z", "spk_1", 0, 1000, "ghost", true));
        Assert.Empty(session.Segments);
        await session.StopAsync();
    }

    // The transcriber's StopAsync flushes the open sentence as a final; the pause guard must
    // latch only after that final is accepted. The final is raised from a worker thread like
    // the real decode thread, so the session's UI marshalling is part of the test (review r6).
    private sealed class FlushOnStopTranscriber : ISpeechTranscriber
    {
        public event Action<Segment>? SegmentReady;
#pragma warning disable CS0067 // the interface requires the event; this fake never fails
        public event Action<string>? Failed;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct)
        {
            IsRunning = false;
            Task.Run(() => SegmentReady?.Invoke(new Segment("flush-1", "spk_1", 0, 500, "kept", true))).Wait();
            return Task.CompletedTask;
        }
    }

    // A flush whose join TIMED OUT: StopAsync returns before the final arrives (the real
    // case: FlushAndWait gave up after its 5 s join budget and the decode thread delivers
    // the sentence later).
    private sealed class LateFlushTranscriber : ISpeechTranscriber
    {
        public event Action<Segment>? SegmentReady;
#pragma warning disable CS0067 // the interface requires the event; this fake never fails
        public event Action<string>? Failed;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct)
        {
            IsRunning = false;
            _ = Task.Run(async () => { await Task.Delay(300); SegmentReady?.Invoke(new Segment("late-1", "spk_1", 0, 500, "late-kept", true)); });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Pause_KeepsALateFlushFinal_WithinTheGrace()
    {
        // A flush whose join timed out delivers the final after the latch: the grace
        // window keeps it instead of dropping the meeting's last sentence (review r21).
        var session = UiLess(() => new MeetingSession(new FakeAudio(), new LateFlushTranscriber(), new FakeStore()));
        await session.StartAsync("mic", "app");
        await session.PauseAsync();
        await Task.Delay(600); // the late final arrives after the latch
        Assert.Contains(session.Segments, s => s.Text == "late-kept");
    }

    // Constructs a session with no ambient context so marshalling runs inline.
    private static MeetingSession UiLess(Func<MeetingSession> make) => TestSyncUtil.UiLess(make);

    [Fact]
    public async Task Pause_KeepsTheFinalFlushedDuringStop()
    {
        var session = UiLess(() => new MeetingSession(new FakeAudio(), new FlushOnStopTranscriber(), new FakeStore()));
        await session.StartAsync("mic", "app");
        await session.PauseAsync();
        var seg = Assert.Single(session.Segments);
        Assert.Equal("kept", seg.Text);
    }

    [Fact]
    public async Task Pause_DrainDeliversTheQueuedFlushFinalBeforeLatching()
    {
        var prev = SynchronizationContext.Current;
        var ctx = new QueueingSyncContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            var session = new MeetingSession(new FakeAudio(), new FlushOnStopTranscriber(), new FakeStore());
            await session.StartAsync("mic", "app");
            var pause = session.PauseAsync();
            // The flush final was posted to the UI "queue"; the pause's yield sits behind
            // it — draining delivers the final first, so it is kept (review r6).
            var drained = ctx.Drain();
            await pause;
            Assert.True(drained >= 2, "expected the queued final and the yielded continuation, drained=" + drained);
            Assert.Equal("kept", Assert.Single(session.Segments).Text);
        }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }
}

