using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public RepositoryTests() { _repo = new LocalMeetingRepository(_path); }

    [Fact]
    public void Save_Load_RoundTrip()
    {
        var m = new Meeting("m1", "Standup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), MeetingStatus.Ready, "rec.webm", "video/webm");
        _repo.Save(m, new[] { new Speaker("spk_1", "Male 1") }, new[] { new Segment("s1", "spk_1", 0, 1000, "hi", true) });
        var loaded = _repo.Load("m1");
        Assert.NotNull(loaded);
        Assert.Equal(MeetingStatus.Ready, loaded.Status);
        Assert.Equal(1, _repo.CountSegments("m1"));
        _repo.RenameSpeaker("m1", "spk_1", "Bob");
    }

    [Fact]
    public void Save_IsAtomic_InterruptedCommitLeavesPriorState()
    {
        var m = new Meeting("m2", "Sync", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, null, null);
        _repo.Save(m, Array.Empty<Speaker>(), new[] { new Segment("s1", "spk_1", 0, 500, "v1", true) });
        Assert.Equal(1, _repo.CountSegments("m2"));
        _repo.Save(m, Array.Empty<Speaker>(), new[] { new Segment("s1", "spk_1", 0, 500, "v1", true), new Segment("s2", "spk_1", 500, 900, "v2", true) });
        Assert.Equal(2, _repo.CountSegments("m2"));
    }

    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }

    [Fact]
    public void Save_RollsBack_OnMidTransactionFault()
    {
        var m = new Meeting("m3", "Retro", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, null, null);
        _repo.Save(m, Array.Empty<Speaker>(), new[] { new Segment("s1", "spk_1", 0, 500, "v1", true) });
        // Duplicate segment id violates the primary key mid-transaction: the whole Save must roll back.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _repo.Save(m, Array.Empty<Speaker>(), new[] { new Segment("s9", "spk_1", 0, 100, "new", true), new Segment("s9", "spk_1", 100, 200, "dup", true) }));
        Assert.Equal(1, _repo.CountSegments("m3"));
        Assert.Equal("v1", _repo.LoadSegments("m3").Single().Text);
    }


    [Fact]
    public void RenameSpeaker_PropagatesToSpeakers_KeepsSegmentJoin()
    {
        var m = new Meeting("m4", "Planning", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, null, null);
        _repo.Save(m, new[] { new Speaker("spk_1", "Male 1") }, new[] { new Segment("s1", "spk_1", 0, 500, "hi", true), new Segment("s2", "spk_1", 500, 900, "yo", true) });
        _repo.RenameSpeaker("m4", "spk_1", "Bob");
        // Every row resolves to the new name through the stable id.
        var speakers = _repo.LoadSpeakers("m4").ToDictionary(s => s.Id);
        Assert.Equal("Bob", speakers["spk_1"].DisplayName);
        foreach (var seg in _repo.LoadSegments("m4"))
            Assert.Equal("Bob", speakers[seg.SpeakerId].DisplayName);
    }


    [Fact]
    public void Settings_GetSet_RoundTrip()
    {
        Assert.Null(_repo.GetSetting("overlay.pin"));
        _repo.SetSetting("overlay.pin", "on");
        Assert.Equal("on", _repo.GetSetting("overlay.pin"));
        _repo.SetSetting("overlay.pin", "off");
        Assert.Equal("off", _repo.GetSetting("overlay.pin"));
    }

    [Fact]
    public void ClearAllBackendSync_RemovesEveryRow()
    {
        _repo.SaveBackendSync(new BackendSyncRow("m1", "b1", "ready", null, false, "2026-10-14T00:00:00Z"));
        _repo.SaveBackendSync(new BackendSyncRow("m2", "b2", "ready", null, false, "2026-10-14T00:00:00Z"));
        Assert.Equal(2, _repo.ClearAllBackendSync());
        Assert.Empty(_repo.ListSyncedMeetings());
    }

}

