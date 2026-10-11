using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class StreamingRunnerTests
{
    private sealed class FakeBackend : IStreamingBackend
    {
        public readonly List<float[]> Fed = new();
        public string Partial = "";
        public string? EndpointText;   // returned once by TryTakeFinal
        public string? FlushText;      // returned once by TryFlushFinal
        public void AcceptWaveform(float[] s) => Fed.Add(s);
        public bool IsReady() => false; // fake/Vosk-style: decode happens inside AcceptWaveform
        public void Decode() { }
        public string CurrentText() => Partial;
        public bool TryTakeFinal(out string text)
        { text = EndpointText ?? ""; var had = EndpointText is not null; EndpointText = null; return had; }
        public bool TryFlushFinal(out string text)
        { text = FlushText ?? ""; var had = FlushText is not null; FlushText = null; return had; }
        public void Dispose() { }
    }

    // A backend whose decode thread can be parked inside AcceptWaveform, so the queue
    // overflows and the join-timeout paths become reachable deterministically.
    private sealed class BlockingBackend : IStreamingBackend
    {
        public readonly ManualResetEventSlim Gate = new(initialState: false);
        public volatile bool Block = true;
        public int Accepts;
        public int CurrentTextCalls;
        public bool Disposed;
        public void AcceptWaveform(float[] s)
        {
            Interlocked.Increment(ref Accepts);
            if (Block) Gate.Wait(TimeSpan.FromSeconds(10));
        }
        public bool IsReady() => false;
        public void Decode() { }
        public string CurrentText() { Interlocked.Increment(ref CurrentTextCalls); return ""; }
        public bool TryTakeFinal(out string text) { text = ""; return false; }
        public bool TryFlushFinal(out string text) { text = ""; return false; }
        public void Dispose() => Disposed = true;
    }

    private static long _clock;
    private static long Next() => ++_clock;

    [Fact]
    public void Partials_AreEmittedOncePerChange()
    {
        var b = new FakeBackend(); var parts = new List<string>();
        using var r = new StreamingRunner(b, Next);
        r.PartialReady += parts.Add;
        r.Start();
        b.Partial = "hello";
        r.Push(new float[] { 1 });
        r.Flush();
        Assert.Contains("hello", parts);
        Assert.Equal(1, parts.Count(p => p == "hello")); // dedup: same text twice = one event
    }

    [Fact]
    public void Endpoint_EmitsFinalSegment_AndResetsUtteranceClock()
    {
        var b = new FakeBackend(); var segs = new List<Segment>();
        using var r = new StreamingRunner(b, Next);
        r.SegmentReady += segs.Add;
        r.Start();
        b.Partial = "hi"; r.Push(new float[] { 1 });
        b.EndpointText = "hi there"; r.Push(new float[] { 2 });
        r.Flush();
        Assert.Single(segs);
        Assert.Equal("hi there", segs[0].Text);
        Assert.True(segs[0].EndMs >= segs[0].StartMs);
    }

    [Fact]
    public void Flush_FinalizesPendingText_AndRunnerRestarts()
    {
        var b = new FakeBackend(); var segs = new List<Segment>();
        using var r = new StreamingRunner(b, Next);
        r.SegmentReady += segs.Add;
        r.Start();
        b.Partial = "unfinished";
        r.Push(new float[] { 1 });
        b.FlushText = "unfinished";   // flush path pulls the text
        r.Flush();
        Assert.Single(segs);
        Assert.Equal("unfinished", segs[0].Text);
        r.Start();                    // a second recording on the same page
        b.EndpointText = "again";
        r.Push(new float[] { 3 });
        r.Flush();
        Assert.Equal(2, segs.Count);
    }

    [Fact]
    public void FullQueue_DropsOldest_AndCounts()
    {
        // With the decode thread parked inside AcceptWaveform, pushing past the 1024-chunk
        // queue forces the drop-oldest branch in Push (the production backpressure path:
        // Push must never block the capture pump).
        var b = new BlockingBackend();
        using var r = new StreamingRunner(b, Next, joinTimeoutMs: 300);
        r.Start();
        r.Push(new float[] { 0 }); // the decode thread takes this one, then blocks on the gate
        Assert.True(SpinWait.SpinUntil(() => b.Accepts >= 1, 5000), "decode thread never reached the backend");
        for (var i = 0; i < 1100; i++) r.Push(new float[] { 1 }); // far beyond the queue capacity
        Assert.True(r.DroppedChunks > 0, "a full queue must drop the oldest chunks");
        b.Block = false; b.Gate.Set();
        Assert.True(r.FlushAndWait()); // the parked thread drains the backlog and exits
    }

    [Fact]
    public void StalledDecodeThread_FlushReportsFalse_AndDisposeKeepsTheBackendAlive()
    {
        // The regression this pins: Flush used to discard the Join result and Dispose always
        // freed the backend, so a stalled decode thread could keep calling into a disposed
        // native recognizer (access violation) — or race a restarted thread over it.
        var b = new BlockingBackend();
        var r = new StreamingRunner(b, Next, joinTimeoutMs: 150);
        r.Start();
        r.Push(new float[] { 0 });
        Assert.True(SpinWait.SpinUntil(() => b.Accepts >= 1, 5000), "decode thread never reached the backend");

        Assert.False(r.FlushAndWait()); // the thread is parked inside AcceptWaveform: reported, not hidden
        r.Dispose();                    // must NOT free the backend under the parked thread
        Assert.False(b.Disposed);

        var before = Volatile.Read(ref b.CurrentTextCalls);
        b.Block = false; b.Gate.Set();  // release the thread; it must finish safely
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref b.CurrentTextCalls) > before, 5000), "the decode loop did not survive Dispose");
        Assert.False(b.Disposed);
    }

    [Fact]
    public void SegmentIds_DoNotCollideAcrossRunnerInstances()
    {
        // transcript_segments.id is a global PRIMARY KEY: two runners (two meetings, or two
        // engines on the same page) must never mint the same id — the first live run of this
        // feature hit UNIQUE-constraint failures exactly this way (both runners started at "s0").
        var b1 = new FakeBackend();
        var segs1 = new List<Segment>();
        using (var r1 = new StreamingRunner(b1, Next))
        {
            r1.SegmentReady += segs1.Add;
            r1.Start();
            b1.EndpointText = "one";
            r1.Push(new float[] { 1 });
            r1.Flush();
        }
        var b2 = new FakeBackend();
        var segs2 = new List<Segment>();
        using (var r2 = new StreamingRunner(b2, Next))
        {
            r2.SegmentReady += segs2.Add;
            r2.Start();
            b2.EndpointText = "two";
            r2.Push(new float[] { 1 });
            r2.Flush();
        }
        Assert.NotEmpty(segs1);
        Assert.NotEmpty(segs2);
        Assert.Empty(segs1.Select(s => s.Id).Intersect(segs2.Select(s => s.Id)));
        // The prefix carries the full GUID (a short slice is only a 32-bit namespace and
        // reintroduces the collision class this test exists to prevent).
        Assert.Matches("^s[0-9a-f]{32}-0$", segs1[0].Id);
    }

    [Fact]
    public void Push_RacingStop_NeverThrowsOutOfTheCapturePump()
    {
        // The capture pump calls Push while Stop/pause can complete the queue mid-call:
        // the _running fast path is racy by nature, so a chunk that loses the race must
        // be a quiet no-op — a throw would surface as CaptureFailed and kill the recording
        // (review round 2: reproduced by the reviewer with ~4,900 throws in 10k races).
        // FlushAndWait completes the queue once per rep, so repeat to make the window hit.
        var errors = new List<Exception>();
        for (var rep = 0; rep < 40; rep++)
        {
            var b = new BlockingBackend();
            using var r = new StreamingRunner(b, Next, joinTimeoutMs: 5);
            r.Start();
            r.Push(new float[] { 0 });
            Assert.True(SpinWait.SpinUntil(() => b.Accepts >= 1, 5000), "decode thread never reached the backend");
            var stop = new ManualResetEventSlim(false);
            var pusher = new Thread(() =>
            {
                try { while (!stop.IsSet) r.Push(new float[] { 1 }); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });
            pusher.Start();
            Assert.True(SpinWait.SpinUntil(() => r.DroppedChunks > 100, 5000), "pusher never filled the queue");
            Assert.False(r.FlushAndWait());   // completes the queue while the pusher keeps pushing
            stop.Set();
            Assert.True(pusher.Join(5000), "pusher thread did not stop");
            b.Block = false; b.Gate.Set();    // release the parked decode thread
        }
        Assert.Empty(errors);
    }
}
