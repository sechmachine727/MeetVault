using MeetVault.Core;

namespace MeetVault.Core.Tests;

sealed class FakeAudio : IAudioCapture
{
    public bool IsCapturing { get; private set; }
    public string? MediaPath => null;
    public string LastMic { get; private set; } = ""; public string LastSource { get; private set; } = ""; public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; LastMic = micId; LastSource = appSource; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    public Task FinishAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
}

sealed class FakeTranscriber : ISpeechTranscriber
{
    public event Action<Segment>? SegmentReady;
    public bool IsRunning { get; private set; }
    public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    public void Emit(Segment s) { if (IsRunning) SegmentReady?.Invoke(s); }
}

sealed class FakeStore : IMeetingStore
{
    public Meeting? Saved; public int SegmentCount; public IReadOnlyList<Speaker>? SavedSpeakers;
    public void Save(Meeting m, IReadOnlyList<Speaker> sp, IReadOnlyList<Segment> se) { Saved = m; SegmentCount = se.Count; SavedSpeakers = sp; } public IReadOnlyList<Segment> LoadSegments(string id) => Array.Empty<Segment>(); public IReadOnlyList<Speaker> LoadSpeakers(string id) => Array.Empty<Speaker>(); public IReadOnlyList<Meeting> LoadUnfinished() => Array.Empty<Meeting>();
}

// A store holding one recoverable draft: the unfinished meeting plus its loaded rows.
sealed class DraftStore : IMeetingStore
{
    private readonly Meeting _draft;
    private readonly IReadOnlyList<Speaker> _speakers;
    private readonly IReadOnlyList<Segment> _segments;
    public DraftStore(Meeting draft, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments) { _draft = draft; _speakers = speakers; _segments = segments; }
    public void Save(Meeting m, IReadOnlyList<Speaker> sp, IReadOnlyList<Segment> se) { }
    public IReadOnlyList<Segment> LoadSegments(string id) => _segments;
    public IReadOnlyList<Speaker> LoadSpeakers(string id) => _speakers;
    public IReadOnlyList<Meeting> LoadUnfinished() => new[] { _draft };
}

// Fires SegmentReady even while stopped: only the session guard may drop segments.
sealed class RawTranscriber : ISpeechTranscriber
{
    public event Action<Segment>? SegmentReady;
    public bool IsRunning { get; private set; }
    public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    public void Emit(Segment s) => SegmentReady?.Invoke(s);
}

// A UI-like context that queues posted callbacks instead of running them inline, so a
// test can hold the UI "behind" the transcription pump and reproduce the real lag.
sealed class QueueingSyncContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public int Drain()
    {
        var n = 0;
        while (_queue.Count > 0) { var (cb, st) = _queue.Dequeue(); cb(st); n++; }
        return n;
    }
}

// Constructs a session with no ambient context, so segment marshalling runs inline
// deterministically in tests.
internal static class TestSyncUtil
{
    public static MeetingSession UiLess(Func<MeetingSession> make)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return make(); } finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }
}

public sealed class SessionTests
{
    [Fact]
    public async Task PauseGap_SkippedAudioNeverReappears()
    {
        var tx = new RawTranscriber();
        var session = TestSyncUtil.UiLess(() => new MeetingSession(new FakeAudio(), tx, new FakeStore()));
        await session.StartAsync("mic", "app");
        tx.Emit(new Segment("a", "spk_1", 0, 1000, "one", true));
        await session.PauseAsync();
        tx.Emit(new Segment("b", "spk_1", 1000, 2000, "ghost", true));
        await session.ResumeAsync();
        tx.Emit(new Segment("c", "spk_1", 3000, 4000, "two", true));
        await session.StopAsync();
        Assert.DoesNotContain(session.Segments, s => s.Text == "ghost");
        Assert.Equal(2, session.Segments.Count);
    }

    [Fact]
    public async Task Segments_AreNamespacedByMeetingId()
    {
        // Base AGENTS rule: transcript_segments.id is a global primary key and ids are
        // namespaced by meeting id (review r11).
        var tx = new RawTranscriber();
        var session = TestSyncUtil.UiLess(() => new MeetingSession(new FakeAudio(), tx, new FakeStore()));
        await session.StartAsync("mic", "app");
        tx.Emit(new Segment("a", "spk_1", 0, 1000, "one", true));
        var seg = Assert.Single(session.Segments);
        Assert.Equal(session.MeetingId + "-a", seg.Id);
    }

    [Fact]
    public async Task Stop_FinalizesMeeting()
    {
        var store = new FakeStore();
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), store);
        await session.StartAsync("mic", "app");
        await session.StopAsync();
        Assert.Equal(MeetingStatus.Ready, store.Saved!.Status);
        Assert.NotNull(store.Saved.EndedAt);
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), new FakeStore());
        await session.StartAsync("mic", "app");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync("mic", "app"));
        await session.StopAsync();
    }

    [Fact]
    public async Task Resume_RestartsSameSources()
    {
        var audio = new FakeAudio();
        var session = new MeetingSession(audio, new RawTranscriber(), new FakeStore());
        await session.StartAsync("mic-A", "app-Zoom");
        await session.PauseAsync();
        await session.ResumeAsync();
        Assert.Equal(("mic-A", "app-Zoom"), (audio.LastMic, audio.LastSource));
        await session.StopAsync();
    }

    // The transcriber raises SegmentReady on its own thread; the session marshals onto
    // the creating (UI) context. A lagging queue must still deliver every segment, in
    // order, each stamped with the constant single-speaker id.
    [Fact]
    public async Task LaggingUiQueue_DeliversEverySegment_StampedOnce()
    {
        var tx = new RawTranscriber();
        var ui = new QueueingSyncContext();
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui); // captured as the session's UI context
        MeetingSession session;
        try { session = new MeetingSession(new FakeAudio(), tx, new FakeStore()); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); } // emission now runs off the UI context
        await session.StartAsync("mic", "app");

        tx.Emit(new Segment("a", "", 0, 1000, "one", true));
        tx.Emit(new Segment("b", "", 1000, 2000, "two", true));

        Assert.Equal(2, ui.Drain()); // both were posted, none ran inline
        Assert.Equal(new[] { "one", "two" }, session.Segments.Select(s => s.Text).ToArray());
        Assert.All(session.Segments, s => Assert.Equal("spk_1", s.SpeakerId));
        await session.StopAsync();
    }

    // The shipped engine has no diarization: the session stamps the schema-required
    // speaker id on segments but never mints or persists speaker rows — "no speakers"
    // is what every surface keys the hide-names rule off.
    [Fact]
    public async Task SingleSpeaker_StampsSegmentsButMintsNoSpeakers()
    {
        var tx = new RawTranscriber();
        var store = new FakeStore();
        var session = new MeetingSession(new FakeAudio(), tx, store);
        await session.StartAsync("mic", "app");

        tx.Emit(new Segment("s1", "", 0, 1000, "hello", true));

        Assert.Single(session.Segments);
        Assert.Equal("spk_1", session.Segments[0].SpeakerId);
        Assert.Empty(session.Speakers);           // no identity was minted
        await session.StopAsync();
        Assert.Empty(store.SavedSpeakers!);       // and none was persisted either
    }

    // Recovery loads the draft's rows verbatim (legacy drafts may carry speaker rows
    // from the retired diarized pipeline) and the new segments keep the constant stamp.
    [Fact]
    public async Task RecoverAsync_LoadsDraftRows_AndContinues()
    {
        var draft = new Meeting("m1", "Meeting", DateTimeOffset.Now, null, MeetingStatus.Recording, null, null);
        var speakers = new[] { new Speaker("spk_1", "A"), new Speaker("spk_2", "B") };
        var segments = new[] { new Segment("s1", "spk_2", 0, 1000, "hello", true) };
        var tx = new RawTranscriber();

        var session = await MeetingSession.RecoverAsync("m1", new FakeAudio(), tx, new DraftStore(draft, speakers, segments), "mic", "app");

        Assert.Equal(2, session.Speakers.Count);   // legacy rows render, nothing new is minted
        tx.Emit(new Segment("s2", "", 1000, 2000, "world", true));
        Assert.Equal(2, session.Segments.Count);
        Assert.Equal("m1-s2", session.Segments[^1].Id);   // meeting-namespaced (review r11)
        Assert.Equal("spk_1", session.Segments[^1].SpeakerId);
        await session.StopAsync();
    }
}
