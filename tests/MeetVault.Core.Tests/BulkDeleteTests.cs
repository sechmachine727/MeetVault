using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Batch deletion must remove every requested meeting and its managed media,
// while reporting per-item failures instead of throwing or half-deleting.
public sealed class BulkDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _db;
    private readonly LocalMeetingRepository _repo;

    public BulkDeleteTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "test.db");
        _repo = new LocalMeetingRepository(_db, _dir);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewMedia(string id)
    {
        var path = Path.Combine(_dir, id + ".m4a");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static Meeting NewMeeting(string id, string? media, MeetingStatus status = MeetingStatus.Ready) =>
        new(id, "t-" + id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, status, media, "audio/m4a");

    private void Save(string id, string? media = null, MeetingStatus status = MeetingStatus.Ready)
    {
        var path = media ?? NewMedia(id);
        _repo.Save(NewMeeting(id, path, status),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-" + id, "spk_1", 0, 1000, "hi", true) });
    }

    [Fact]
    public void DeleteMeetings_RemovesAllMeetingsSegmentsSpeakersAndMedia()
    {
        Save("a"); Save("b"); Save("c");
        var mediaA = _repo.Load("a")!.MediaPath!;

        var result = _repo.DeleteMeetings(new[] { "a", "b", "c" });

        Assert.Equal(3, result.DeletedIds.Count);
        Assert.Empty(result.FailedIds);
        foreach (var id in new[] { "a", "b", "c" })
        {
            Assert.Null(_repo.Load(id));
            Assert.Empty(_repo.LoadSegments(id));
            Assert.Empty(_repo.LoadSpeakers(id));
            Assert.DoesNotContain(_repo.ListMeetings(), x => x.Id == id);
        }
        Assert.False(File.Exists(mediaA));
    }

    [Fact]
    public void DeleteMeetings_UnknownId_ReportedNotThrown()
    {
        var result = _repo.DeleteMeetings(new[] { "nope" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("nope"));
    }

    [Fact]
    public void DeleteMeetings_DuplicateIds_DeletedOnce()
    {
        Save("a");

        var result = _repo.DeleteMeetings(new[] { "a", "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_LockedMedia_KeepsRowAndReports()
    {
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        using var locked = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.NotNull(_repo.Load("a"));
    }

    [Theory]
    [InlineData(MeetingStatus.Recording)]
    [InlineData(MeetingStatus.Paused)]
    public void DeleteMeetings_ActiveOrResumable_Rejected(MeetingStatus status)
    {
        Save("a", status: status);
        var media = _repo.Load("a")!.MediaPath!;

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.NotNull(_repo.Load("a"));
        Assert.True(File.Exists(media));
    }

    [Fact]
    public void DeleteMeetings_MediaOutsideManagedDir_NeverTouched()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            _repo.Save(NewMeeting("a", outside),
                new[] { new Speaker("spk_1", "Speaker 1") },
                new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

            var result = _repo.DeleteMeetings(new[] { "a" });

            Assert.Empty(result.DeletedIds);
            Assert.True(result.FailedIds.ContainsKey("a"));
            Assert.NotNull(_repo.Load("a"));
            Assert.True(File.Exists(outside));
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void DeleteMeetings_PartialFailure_OthersStillDeleted()
    {
        Save("good");
        Save("locked");
        var lockedMedia = _repo.Load("locked")!.MediaPath!;
        using var held = new FileStream(lockedMedia, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = _repo.DeleteMeetings(new[] { "good", "locked", "missing" });

        Assert.Equal(new[] { "good" }, result.DeletedIds.ToArray());
        Assert.Equal(2, result.FailedIds.Count);
        Assert.Null(_repo.Load("good"));
        Assert.NotNull(_repo.Load("locked"));
    }
}
