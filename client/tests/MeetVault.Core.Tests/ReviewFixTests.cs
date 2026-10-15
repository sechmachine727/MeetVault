using MeetVault.Core;

namespace MeetVault.Core.Tests;

// TDD RED for independent-review findings (Fermat): crash durability + validation.
// Minimal: fail first, then smallest Core fix.
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public ReviewFixTests() { _repo = new LocalMeetingRepository(_path); }
    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }

    [Fact]
    public async Task Start_PersistsDraft_RecoverableWithoutCheckpoint()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), _repo);
        await session.StartAsync("mic", "app");
        // simulate crash: no Checkpoint, no Stop — draft must already exist
        var unfinished = _repo.LoadUnfinished();
        Assert.Single(unfinished);
    }

    [Fact]
    public async Task Recover_EmptyMicId_ThrowsActionable()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), _repo);
        await session.StartAsync("mic", "app");
        await session.CheckpointAsync();
        var id = _repo.LoadUnfinished().Single().Id;
        await Assert.ThrowsAsync<ArgumentException>(() => MeetingSession.RecoverAsync(id, new FakeAudio(), new RawTranscriber(), _repo, "", ""));
    }

    [Fact]
    public void Load_BadEnum_DoesNotThrow()
    {
        var m = new Meeting("bad-enum", "t", DateTimeOffset.UtcNow, null, MeetingStatus.Recording, null, null);
        _repo.Save(m, Array.Empty<Speaker>(), Array.Empty<Segment>());
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
        conn.Open();
        using (var c = conn.CreateCommand()) { c.CommandText = "UPDATE meetings SET status='FutureStatus' WHERE id='bad-enum'"; c.ExecuteNonQuery(); }
        var loaded = _repo.Load("bad-enum");
        Assert.NotNull(loaded);
    }

    [Fact]
    public void RenameSpeaker_EmptyName_ThrowsActionable()
    {
        var m = new Meeting("rn", "t", DateTimeOffset.UtcNow, null, MeetingStatus.Recording, null, null);
        _repo.Save(m, new[] { new Speaker("spk_1", "Speaker 1") }, Array.Empty<Segment>());
        Assert.Throws<ArgumentException>(() => _repo.RenameSpeaker("rn", "spk_1", ""));
    }
}
