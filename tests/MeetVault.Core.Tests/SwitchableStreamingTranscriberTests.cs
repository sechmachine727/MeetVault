using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class SwitchableStreamingTranscriberTests
{
    private sealed class FakeEngine : IStreamingTranscriber
    {
        public FakeEngine(string dir) { Dir = dir; }
        public string Dir { get; }
        public List<float[]> Received { get; } = new();
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public bool Flushed { get; private set; }
        public bool IsRunning => Started && !Stopped;
        public event Action<Segment>? SegmentReady;
        public event Action<string>? PartialReady;
        public Task StartAsync(CancellationToken ct) { Started = true; Stopped = false; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
        public void Push(float[] samples16k) => Received.Add(samples16k);
        public void Flush() => Flushed = true;
        public void Dispose() => Disposed = true;
        public void RaiseSegment(string text) => SegmentReady?.Invoke(new Segment("s1", "", 0, 1, text, true));
        public void RaisePartial(string text) => PartialReady?.Invoke(text);
    }

    private sealed class Factory
    {
        public readonly List<FakeEngine> Created = new();
        public bool FailNext;
        public FakeEngine Create(string dir)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("model load failed"); }
            var engine = new FakeEngine(dir);
            Created.Add(engine);
            return engine;
        }
    }

    [Fact]
    public void Start_ThenSwap_ReplaysPreRoll()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        var a = new float[] { 1, 2 };
        var b = new float[] { 3, 4 };
        relay.StartAsync(CancellationToken.None);         // no engine yet: auto-listen
        relay.Push(a);                                    // buffered into the pre-roll
        Assert.True(relay.SwapTo("dir-A"));
        relay.Push(b);                                    // straight through
        var engine = Assert.Single(factory.Created);
        Assert.Equal("dir-A", engine.Dir);
        Assert.True(engine.Started);
        Assert.Equal(2, engine.Received.Count);
        Assert.Equal(a, engine.Received[0]);
        Assert.Equal(b, engine.Received[1]);
    }

    [Fact]
    public void Swap_ForwardsFinalFromOldEngine_AndDetachesTheOld()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        var segments = new List<string>();
        relay.SegmentReady += s => segments.Add(s.Text);
        relay.StartAsync(CancellationToken.None);
        Assert.True(relay.SwapTo("dir-A"));
        var a = factory.Created[0];
        a.RaiseSegment("from-A");
        Assert.True(relay.SwapTo("dir-B"));               // flush-first, then detach
        Assert.True(a.Flushed);
        Assert.True(a.Disposed);
        a.RaiseSegment("late-from-A");                    // must NOT surface
        var b = factory.Created[1];
        b.RaiseSegment("from-B");
        Assert.Equal(new[] { "from-A", "from-B" }, segments.ToArray());
    }

    [Fact]
    public void Swap_SameDir_IsNoOp()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        relay.StartAsync(CancellationToken.None);
        Assert.True(relay.SwapTo("dir-A"));
        Assert.True(relay.SwapTo("dir-A"));
        Assert.Single(factory.Created);
    }

    [Fact]
    public void Swap_WhilePaused_NewEngineStartsOnResume()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        relay.StartAsync(CancellationToken.None);
        relay.SwapTo("dir-A");
        relay.StopAsync(CancellationToken.None);
        Assert.True(relay.SwapTo("dir-B"));
        var b = factory.Created[1];
        Assert.False(b.Started);
        relay.StartAsync(CancellationToken.None);
        Assert.True(b.Started);
    }

    [Fact]
    public void FactoryThrows_ReturnsFalse_BuffersThenRecovers()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        relay.StartAsync(CancellationToken.None);
        factory.FailNext = true;
        Assert.False(relay.SwapTo("dir-A"));
        relay.Push(new float[] { 9 });                    // buffered into the pre-roll
        Assert.True(relay.SwapTo("dir-A"));               // retry succeeds
        var engine = Assert.Single(factory.Created);
        Assert.True(engine.Started);
        Assert.Single(engine.Received);                   // the buffered chunk replayed
    }

    [Fact]
    public void Dispose_DisposesInner_AndStopsForwarding()
    {
        var factory = new Factory();
        var relay = new SwitchableStreamingTranscriber(factory.Create);
        var segments = new List<string>();
        relay.SegmentReady += s => segments.Add(s.Text);
        relay.StartAsync(CancellationToken.None);
        Assert.True(relay.SwapTo("dir-A"));
        var a = factory.Created[0];
        relay.Dispose();
        Assert.True(a.Disposed);
        a.RaiseSegment("after-dispose");
        Assert.Empty(segments);
    }
}
