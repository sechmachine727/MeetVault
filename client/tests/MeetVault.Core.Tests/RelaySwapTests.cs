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
    public event Action<string>? Failed;
        public event Action<string>? PartialReady;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public TaskCompletionSource? StopGate;
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return StopGate?.Task ?? Task.CompletedTask; }
        public void Push(float[] samples16k) => Pushes.Add(samples16k);
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
    public async Task StopAsync_AwaitsTheInnerEngine()
    {
        // The pause latch ordering depends on the relay's stop really waiting for the
        // engine's flush; StopAsync used to return an already-completed task (review r10).
        var a = new Rec();
        var relay = new SwitchableStreamingTranscriber(_ => a);
        Assert.True(await Task.Run(() => relay.SwapTo("a")));
        await relay.StartAsync(CancellationToken.None);
        a.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = relay.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);          // still waiting on the engine
        a.StopGate.SetResult();
        await stop;
    }

    [Fact]
    public async Task SwapAfterDispose_IsRefusedAndBuildsNothing()
    {
        // A swap starting after Dispose must not build/subscribe/publish an engine that
        // nothing owns (review r9): OnUnloaded can dispose the relay while ApplyLanguage's
        // Task.Run swap is still queued behind it.
        var built = 0;
        var relay = new SwitchableStreamingTranscriber(_ => { built++; return new Rec(); });
        Assert.True(await Task.Run(() => relay.SwapTo("a")));
        relay.Dispose();
        Assert.False(await Task.Run(() => relay.SwapTo("b")));
        Assert.Equal(1, built);   // the refused swap built no engine
    }

    [Fact]
    public async Task OnlyIfUnset_RefusesWhenAnEngineExists()
    {
        // The auto-start fallback must not overwrite an engine published while it waited
        // on the swap gate (review r14).
        var relay = new SwitchableStreamingTranscriber(_ => new Rec());
        Assert.True(await Task.Run(() => relay.SwapTo("a", onlyIfNoEngine: true)));
        Assert.False(await Task.Run(() => relay.SwapTo("b", onlyIfNoEngine: true)));
        Assert.Equal("a", relay.ActiveModelDir);
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
