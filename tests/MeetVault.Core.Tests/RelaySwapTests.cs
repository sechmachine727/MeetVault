using MeetVault.Core;

namespace MeetVault.Core.Tests;

// A swap that cannot build or start its replacement must leave the OLD engine serving the
// meeting (review r7): the relay used to detach and dispose the old engine FIRST, so a
// factory failure left it deaf once the pre-roll filled. Also pins the same-model fast path.
public sealed class RelaySwapTests
{
    private sealed class Rec : IStreamingTranscriber
    {
        public bool IsRunning { get; private set; }
        public List<float[]> Pushes { get; } = new();
#pragma warning disable CS0067 // the interface requires the events; this fake never raises them
        public event Action<Segment>? SegmentReady;
        public event Action<string>? PartialReady;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public void Push(float[] samples16k) => Pushes.Add(samples16k);
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task FactoryFailure_KeepsTheOldEngineServing()
    {
        var first = new Rec();
        var relay = new SwitchableStreamingTranscriber(dir =>
            dir == "a" ? first : throw new InvalidOperationException("model missing"));
        Assert.True(await Task.Run(() => relay.SwapTo("a")));
        await relay.StartAsync(CancellationToken.None);
        relay.Push(new float[16]);

        Assert.False(await Task.Run(() => relay.SwapTo("b")));   // the failed swap
        relay.Push(new float[16]);
        relay.Push(new float[16]);

        Assert.Equal(3, first.Pushes.Count);        // the old engine never stopped serving
        Assert.Equal("a", relay.ActiveModelDir);    // and the failure did not take over the id
    }

    [Fact]
    public async Task SameModelSwap_IsANoOp()
    {
        var built = 0;
        var relay = new SwitchableStreamingTranscriber(_ => { built++; return new Rec(); });
        Assert.True(await Task.Run(() => relay.SwapTo("a")));
        Assert.True(await Task.Run(() => relay.SwapTo("a")));
        Assert.Equal(1, built);                     // the second call reused the live engine
    }
}
