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
        var media = new[] { "a", "b", "c" }.ToDictionary(id => id, id => _repo.Load(id)!.MediaPath!);

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
        foreach (var path in media.Values)
        {
            Assert.False(File.Exists(path), "media must be deleted: " + path);
            Assert.False(File.Exists(path + ".deleting"), "a successful delete must not leave an aside file: " + path);
        }
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
    public void DeleteMeetings_MediaOutsideManagedDir_RowDeleted_FileUntouched()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            _repo.Save(NewMeeting("a", outside),
                new[] { new Speaker("spk_1", "Speaker 1") },
                new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

            var result = _repo.DeleteMeetings(new[] { "a" });

            // Unmanaged media never blocks the row (review fix): the meeting must be
            // deletable; the foreign file itself is never touched.
            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Empty(result.FailedIds);
            Assert.Null(_repo.Load("a"));
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

    // --- review hardening (independent review deleg_d2ddb14e task-1) ---

    [Fact]
    public void DeleteMeetings_NullOrEmptyId_Skipped_NoThrow()
    {
        Save("a");

        var result = _repo.DeleteMeetings(new[] { "a", null!, "" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_EmptyCollection_EmptyResult()
    {
        var result = _repo.DeleteMeetings(Array.Empty<string>());

        Assert.Empty(result.DeletedIds);
        Assert.Empty(result.FailedIds);
    }

    [Fact]
    public void DeleteMeetings_MediaRootWithTrailingSeparator_StillDeletes()
    {
        var dir2 = Path.Combine(_dir, "media2");
        Directory.CreateDirectory(dir2);
        using var repo2 = new LocalMeetingRepository(Path.Combine(dir2, "t2.db"), dir2 + Path.DirectorySeparatorChar);
        var media = Path.Combine(dir2, "x.m4a");
        File.WriteAllBytes(media, new byte[] { 1 });
        repo2.Save(NewMeeting("a", media),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

        var result = repo2.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.False(File.Exists(media));
        Assert.Null(repo2.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_RelativeMediaPath_RowDeleted_FileUntouched()
    {
        _repo.Save(NewMeeting("a", "sub" + Path.DirectorySeparatorChar + "rel.m4a"),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

        var result = _repo.DeleteMeetings(new[] { "a" });

        // A relative path is unmanaged (never a delete target); the row still goes.
        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_TraversalRow_RowDeleted_OutsideUntouched()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            _repo.Save(NewMeeting("a", Path.Combine(_dir, "..", Path.GetFileName(outside))),
                new[] { new Speaker("spk_1", "Speaker 1") },
                new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

            var result = _repo.DeleteMeetings(new[] { "a" });

            // The traversal path never becomes a delete target; the row still goes.
            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Empty(result.FailedIds);
            Assert.True(File.Exists(outside));
            Assert.Null(_repo.Load("a"));
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void DeleteMeetings_NullMediaPath_RowDeleted()
    {
        _repo.Save(NewMeeting("a", null),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_MediaAlreadyMissing_RowStillDeleted()
    {
        Save("a");
        File.Delete(_repo.Load("a")!.MediaPath!);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_StaleAsideAfterCrash_ReclaimedOnRetry()
    {
        // The state a crash between the aside rename and the row delete leaves behind:
        // the media sits at <media>.deleting and the original path is gone. The retry
        // must delete the row AND reclaim the aside — otherwise the bytes survive with
        // nothing pointing at them.
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        var aside = media + ".deleting";
        File.Move(media, aside);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Null(_repo.Load("a"));
        Assert.False(File.Exists(aside), "the retry must reclaim the stale aside");
        Assert.False(File.Exists(media));
    }

    [Theory]
    [InlineData(MeetingStatus.Failed)]
    [InlineData(MeetingStatus.Processing)]
    public void DeleteMeetings_NonActiveStatuses_Deletable(MeetingStatus status)
    {
        Save("a", status: status);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_LockThenRelease_RetrySucceeds()
    {
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        using (var hold = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = _repo.DeleteMeetings(new[] { "a" });
            Assert.Empty(first.DeletedIds);
            Assert.True(first.FailedIds.ContainsKey("a"));
        }

        var retry = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, retry.DeletedIds.ToArray());
        Assert.False(File.Exists(media));
        Assert.False(File.Exists(media + ".deleting"), "the successful retry must not leave an aside file behind");
        Assert.Null(_repo.Load("a"));
    }

    // --- review round 2 (rename-aside ordering) ---

    [Fact]
    public void DeleteMeetings_RowDeleteFails_FileRestored_RowKept()
    {
        // Force the row delete to fail AFTER the aside rename: a BEFORE DELETE trigger on
        // meetings aborts the DELETE deterministically and instantly (a competing write
        // lock would only fail after the 30 s busy timeout). The renamed recording must be
        // put back so a failed row delete cannot lose the audio.
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "CREATE TRIGGER block_del BEFORE DELETE ON meetings BEGIN SELECT RAISE(ABORT, 'forced row-delete failure'); END;";
            c.ExecuteNonQuery();
        }

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.NotNull(_repo.Load("a"));
        Assert.True(File.Exists(media), "a failed row delete must restore the renamed recording");
        Assert.False(File.Exists(media + ".deleting"), "a restored delete must not leave an aside file behind");
    }
}
