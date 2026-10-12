using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class MeetingLanguageTests : IDisposable
{
    private readonly string _dir;
    private readonly LocalMeetingRepository _repo;

    public MeetingLanguageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mv-lang-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _repo = new LocalMeetingRepository(Path.Combine(_dir, "t.db"), _dir);
    }

    public void Dispose()
    {
        try { _repo.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Meeting NewMeeting(string id) => new(id, "T " + id, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow, MeetingStatus.Ready, null, null);

    [Fact]
    public void SetMeetingLanguage_RoundTrips()
    {
        _repo.Save(NewMeeting("a"), Array.Empty<Speaker>(), Array.Empty<Segment>());
        _repo.SetMeetingLanguage("a", "ja");
        Assert.Equal("ja", _repo.Load("a")!.Language);
        Assert.Equal("ja", _repo.ListMeetings().Single(m => m.Id == "a").Language);
    }

    [Fact]
    public void CheckpointSave_DoesNotClobberTheLanguage()
    {
        _repo.Save(NewMeeting("a"), Array.Empty<Speaker>(), Array.Empty<Segment>());
        Assert.Equal("", _repo.Load("a")!.Language);                       // default until the first write
        _repo.SetMeetingLanguage("a", "vi");
        _repo.Save(NewMeeting("a"), Array.Empty<Speaker>(), Array.Empty<Segment>());   // checkpoint-style save
        Assert.Equal("vi", _repo.Load("a")!.Language);
    }

    [Fact]
    public void LegacyDatabase_WithoutLanguageColumn_IsMigratedOnOpen()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            con.Open();
            using var c = con.CreateCommand();
            c.CommandText = "CREATE TABLE meetings(id TEXT PRIMARY KEY, title TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT NULL, duration_ms INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL, media_path TEXT NULL, media_mime TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);";
            c.ExecuteNonQuery();
        }
        using var repo = new LocalMeetingRepository(path, _dir);           // must ALTER on open
        repo.Save(NewMeeting("b"), Array.Empty<Speaker>(), Array.Empty<Segment>());
        Assert.Equal("", repo.Load("b")!.Language);                        // reads fine on the migrated schema
        repo.SetMeetingLanguage("b", "en");
        Assert.Equal("en", repo.Load("b")!.Language);
    }
}
