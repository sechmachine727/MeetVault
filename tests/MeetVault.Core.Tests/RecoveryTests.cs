using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Uses the real SQLite repo as the store: crash recovery is a store round-trip.
public sealed class RecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public RecoveryTests() { _repo = new LocalMeetingRepository(_path); }

    [Fact]
    public async Task CrashDraft_Reloads_AsRecoverable()
    {
        var tx = new RawTranscriber();
        var session = new MeetingSession(new FakeAudio(), tx, new FakeDiarizer(), new FakeGender(), _repo);
        await session.StartAsync("mic", "app");
        tx.Emit(new Segment("a", "spk_1", 0, 1000, "before-crash", true));
        await session.CheckpointAsync(); // simulate crash: no StopAsync, process dies here
        var unfinished = _repo.LoadUnfinished();
        Assert.Single(unfinished);
        var recovered = await MeetingSession.RecoverAsync(unfinished[0].Id, new FakeAudio(), tx, new FakeDiarizer(), new FakeGender(), _repo);
        Assert.Equal("before-crash", recovered.Segments.Single().Text);
        tx.Emit(new Segment("b", "spk_1", 1000, 2000, "after-restart", true));
        await recovered.StopAsync();
        Assert.Equal(2, _repo.LoadSegments(unfinished[0].Id).Count);
    }

    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }
}

