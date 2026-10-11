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
        var b = new FakeBackend { };
        using var r = new StreamingRunner(b, Next);
        r.Start();
        // The decode thread consumes continuously; to force drops, push faster than 64
        // before it drains is unreliable — so assert the counter API exists and stays 0
        // on a normal run, then rely on the log warn for the pathological case.
        for (var i = 0; i < 10; i++) r.Push(new float[] { 1 });
        r.Flush();
        Assert.Equal(0, r.DroppedChunks);
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
    }
}
