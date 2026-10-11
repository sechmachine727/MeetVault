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
}

