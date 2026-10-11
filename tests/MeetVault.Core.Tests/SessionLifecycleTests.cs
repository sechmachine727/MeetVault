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

    // The transcriber's StopAsync flushes the open sentence as a final; the pause guard
    // must latch AFTER that final is accepted (review r5: latching first dropped it).
    private sealed class FlushOnStopTranscriber : ISpeechTranscriber
    {
        public event Action<Segment>? SegmentReady;
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct)
        {
            IsRunning = false;
            SegmentReady?.Invoke(new Segment("flush-1", "spk_1", 0, 500, "kept", true));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Pause_KeepsTheFinalFlushedDuringStop()
    {
        var session = new MeetingSession(new FakeAudio(), new FlushOnStopTranscriber(), new FakeStore());
        await session.StartAsync("mic", "app");
        await session.PauseAsync();
        var seg = Assert.Single(session.Segments);
        Assert.Equal("kept", seg.Text);
    }
}

