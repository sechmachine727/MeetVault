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

sealed class FakeDiarizer : ISpeakerDiarizer
{
    public SpeakerHint AssignTurn(TurnAudio turn) => new("spk_1", SpeakerOrigin.Unknown);
}

sealed class FakeGender : IGenderLabeler
{
    public string Label(string speakerId, Gender estimate) => "Male 1";
}

sealed class FakeStore : IMeetingStore
{
    public Meeting? Saved; public int SegmentCount;
    public void Save(Meeting m, IReadOnlyList<Speaker> sp, IReadOnlyList<Segment> se) { Saved = m; SegmentCount = se.Count; } public IReadOnlyList<Segment> LoadSegments(string id) => Array.Empty<Segment>(); public IReadOnlyList<Speaker> LoadSpeakers(string id) => Array.Empty<Speaker>(); public IReadOnlyList<Meeting> LoadUnfinished() => Array.Empty<Meeting>();
}


sealed class ScriptedDiarizer : ISpeakerDiarizer
{
    private readonly Queue<string> _turns;
    public ScriptedDiarizer(params string[] turns) { _turns = new Queue<string>(turns); }
    public SpeakerHint AssignTurn(TurnAudio turn) => new(_turns.Count > 0 ? _turns.Dequeue() : "spk_x", SpeakerOrigin.Embedding);
}

// Records every turn it is asked to resolve; used to prove one diarization per turn.
sealed class RecordingDiarizer : ISpeakerDiarizer
{
    public readonly List<TurnAudio> Turns = new();
    public SpeakerHint AssignTurn(TurnAudio turn) { Turns.Add(turn); return new("spk_1", SpeakerOrigin.Unknown); }
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

public sealed class SessionTests
{
    [Fact]
    public async Task PauseGap_SkippedAudioNeverReappears()
    {
        var tx = new RawTranscriber();
        var session = new MeetingSession(new FakeAudio(), tx, new FakeDiarizer(), new FakeGender(), new FakeStore());
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
    public async Task Stop_FinalizesMeeting()
    {
        var store = new FakeStore();
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), new FakeDiarizer(), new FakeGender(), store);
        await session.StartAsync("mic", "app");
        await session.StopAsync();
        Assert.Equal(MeetingStatus.Ready, store.Saved!.Status);
        Assert.NotNull(store.Saved.EndedAt);
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), new FakeDiarizer(), new FakeGender(), new FakeStore());
        await session.StartAsync("mic", "app");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync("mic", "app"));
        await session.StopAsync();
    }

    [Fact]
    public async Task Resume_RestartsSameSources()
    {
        var audio = new FakeAudio();
        var session = new MeetingSession(audio, new RawTranscriber(), new FakeDiarizer(), new FakeGender(), new FakeStore());
        await session.StartAsync("mic-A", "app-Zoom");
        await session.PauseAsync();
        await session.ResumeAsync();
        Assert.Equal(("mic-A", "app-Zoom"), (audio.LastMic, audio.LastSource));
        await session.StopAsync();
    }


    [Fact]
    public async Task Session_UsesSeparateGenderCounters()
    {
        var tx = new RawTranscriber();
        var diarizer = new ScriptedDiarizer("spk_m", "spk_f", "spk_m2");
        var session = new MeetingSession(new FakeAudio(), tx, diarizer, new CounterGenderLabeler(id => id.StartsWith("spk_m") ? Gender.Male : Gender.Female), new FakeStore());
        await session.StartAsync("mic", "app");
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 0, 1000));
        tx.Emit(new Segment("a", "", 0, 1000, "one", true));
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 1000, 2000));
        tx.Emit(new Segment("b", "", 1000, 2000, "two", true));
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 2000, 3000));
        tx.Emit(new Segment("c", "", 2000, 3000, "three", true));
        await session.StopAsync();
        var names = session.Speakers.OrderBy(s => s.Id).Select(s => s.DisplayName).ToArray();
        Assert.Equal(new[] { "Female 1", "Male 1", "Male 2" }, names);
    }

    [Fact]
    public async Task NoteTurnAudio_OneDiarizationPerTurn_SegmentsShareTheHint()
    {
        var tx = new RawTranscriber();
        var recording = new RecordingDiarizer();
        var session = new MeetingSession(new FakeAudio(), tx, recording, new FakeGender(), new FakeStore());
        await session.StartAsync("mic", "app");

        session.NoteTurnAudio(new TurnAudio(new float[160], 0, 100));
        tx.Emit(new Segment("a", "", 0, 50, "one", true));
        tx.Emit(new Segment("b", "", 50, 100, "two", true));

        Assert.Single(recording.Turns);
        Assert.Equal(0, recording.Turns[0].StartMs);
        Assert.Equal(2, session.Segments.Count);
        Assert.All(session.Segments, s => Assert.Equal("spk_1", s.SpeakerId));
        await session.StopAsync();
    }

    [Fact]
    public async Task NoteTurnAudio_UnknownHint_KeepsPreviousSpeaker()
    {
        var tx = new RawTranscriber();
        var session = new MeetingSession(new FakeAudio(), tx, new ScriptedDiarizer("spk_1", "", "spk_2"), new FakeGender(), new FakeStore());
        await session.StartAsync("mic", "app");

        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 0, 1000));
        tx.Emit(new Segment("a", "", 0, 1000, "one", true));
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 1000, 2000));
        tx.Emit(new Segment("b", "", 1000, 2000, "two", true));  // Unknown hint -> previous speaker
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 2000, 3000));
        tx.Emit(new Segment("c", "", 2000, 3000, "three", true));

        Assert.Equal(new[] { "spk_1", "spk_1", "spk_2" }, session.Segments.Select(s => s.SpeakerId).ToArray());
        await session.StopAsync();
    }

    [Fact]
    public async Task LaggingUiQueue_StampsEachTurnsSegmentsWithItsOwnSpeaker()
    {
        var tx = new RawTranscriber();
        var diarizer = new ScriptedDiarizer("spk_a", "spk_b");
        var ui = new QueueingSyncContext();
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui); // captured as the session's UI context
        MeetingSession session;
        try { session = new MeetingSession(new FakeAudio(), tx, diarizer, new FakeGender(), new FakeStore()); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); } // emission now runs off the UI context
        await session.StartAsync("mic", "app");

        // Turn A resolves to spk_a; its segment is posted to the (lagging) UI queue.
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 0, 1000));
        tx.Emit(new Segment("a", "", 0, 1000, "one", true));

        // Turn B is resolved BEFORE the UI drains turn A: with the old code the drain read
        // the field late and stamped BOTH segments with spk_b (turn B's speaker).
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 1000, 2000));
        tx.Emit(new Segment("b", "", 1000, 2000, "two", true));

        Assert.Equal(2, ui.Drain());
        Assert.Equal(new[] { "spk_a", "spk_b" }, session.Segments.Select(s => s.SpeakerId).ToArray());
        await session.StopAsync();
    }
}








