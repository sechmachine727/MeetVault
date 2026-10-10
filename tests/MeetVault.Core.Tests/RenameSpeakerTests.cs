using MeetVault.Core;

namespace MeetVault.Core.Tests;

// FR2: renaming a speaker updates the in-memory label and persists with the draft.
public sealed class RenameSpeakerTests
{
    private sealed class CapturingStore : IMeetingStore
    {
        public IReadOnlyList<Speaker> LastSpeakers { get; private set; } = Array.Empty<Speaker>();
        public int SaveCount { get; private set; }
        public void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments) { LastSpeakers = speakers.ToList(); SaveCount++; }
        public IReadOnlyList<Segment> LoadSegments(string meetingId) => Array.Empty<Segment>();
        public IReadOnlyList<Speaker> LoadSpeakers(string meetingId) => Array.Empty<Speaker>();
        public IReadOnlyList<Meeting> LoadUnfinished() => Array.Empty<Meeting>();
    }

    private sealed class NoopAudio : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public string? MediaPath => null;
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
        public Task FinishAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    }

    private sealed class EmittingTranscriber : ISpeechTranscriber
    {
        public event Action<Segment>? SegmentReady;
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public void Emit(Segment s) => SegmentReady?.Invoke(s);
    }

    private sealed class OneSpeaker : ISpeakerDiarizer { public SpeakerHint AssignTurn(TurnAudio turn) => new("spk_1", SpeakerOrigin.Unknown, null); }
    private sealed class NeutralGender : IGenderLabeler { public string Label(string speakerId, Gender estimate) => "Male 1"; }

    [Fact]
    public async Task Rename_UpdatesLabelAndPersists()
    {
        var tx = new EmittingTranscriber();
        var store = new CapturingStore();
        var session = new MeetingSession(new NoopAudio(), tx, new OneSpeaker(), new NeutralGender(), store);
        await session.StartAsync("mic", "app");
        tx.Emit(new Segment("a", "spk_1", 0, 1000, "hello", true));
        var before = store.SaveCount;

        session.RenameSpeaker("spk_1", "Alice");

        Assert.Equal("Alice", session.Speakers.Single(s => s.Id == "spk_1").DisplayName);
        Assert.Equal("Alice", store.LastSpeakers.Single(s => s.Id == "spk_1").DisplayName);
        Assert.True(store.SaveCount > before, "rename should persist a checkpoint");
        await session.StopAsync();
    }

    [Fact]
    public async Task Rename_BlankOrUnknown_IsIgnored()
    {
        var store = new CapturingStore();
        var session = new MeetingSession(new NoopAudio(), new EmittingTranscriber(), new OneSpeaker(), new NeutralGender(), store);
        await session.StartAsync("mic", "app");
        session.RenameSpeaker("spk_missing", "Bob"); // unknown id: no throw
        session.RenameSpeaker("spk_1", "   ");       // blank name: ignored
        await session.StopAsync();
    }

    // Phase 3: a manual rename must survive later automatic assignments of the same id.
    [Fact]
    public async Task Rename_SurvivesLaterAutomaticAssignments()
    {
        var tx = new EmittingTranscriber();
        var store = new CapturingStore();
        var session = new MeetingSession(new NoopAudio(), tx, new ScriptedDiarizer("spk_1", "spk_1", "spk_2"), new NeutralGender(), store);
        await session.StartAsync("mic", "app");

        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 0, 1000));
        tx.Emit(new Segment("a", "", 0, 1000, "hello", true));
        session.RenameSpeaker("spk_1", "Alice");

        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 1000, 2000));
        tx.Emit(new Segment("b", "", 1000, 2000, "again", true));
        session.NoteTurnAudio(new TurnAudio(ReadOnlyMemory<float>.Empty, 2000, 3000));
        tx.Emit(new Segment("c", "", 2000, 3000, "other", true));

        Assert.Equal("Alice", session.Speakers.Single(s => s.Id == "spk_1").DisplayName);
        Assert.Equal(2, session.Speakers.Count);
        Assert.Equal("Alice", store.LastSpeakers.Single(s => s.Id == "spk_1").DisplayName);
        await session.StopAsync();
    }
}
