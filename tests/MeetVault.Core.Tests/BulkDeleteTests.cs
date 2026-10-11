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
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
    }

    [Fact]
    public void DeleteMeetings_DuplicateIds_DeletedOnce()
    {
        Save("a");

        var result = _repo.DeleteMeetings(new[] { "a", "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
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
            // deletable; the foreign file itself is never touched. The survivor is
            // reported (round 9) so the UI can warn instead of claiming the audio went.
            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Empty(result.FailedIds);
            var skip = Assert.Single(result.SkippedMedia);
            Assert.Equal("a", skip.Id);
            Assert.Equal(outside, skip.Path);
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
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_EmptyCollection_EmptyResult()
    {
        var result = _repo.DeleteMeetings(Array.Empty<string>());

        Assert.Empty(result.DeletedIds);
        Assert.Empty(result.FailedIds);
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
        Assert.False(File.Exists(media));
        Assert.Null(repo2.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_MissingUnmanagedPath_RowDeleted_NotReported()
    {
        _repo.Save(NewMeeting("a", "sub" + Path.DirectorySeparatorChar + "rel.m4a"),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-a", "spk_1", 0, 1000, "hi", true) });

        var result = _repo.DeleteMeetings(new[] { "a" });

        // A relative path is unmanaged (never a delete target) and the file never
        // existed: the row still goes, and with nothing surviving there is nothing to
        // report — "could not be removed" would be false.
        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Empty(result.SkippedMedia);
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

            // The traversal path never becomes a delete target; the row still goes and
            // the surviving path is reported.
            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Empty(result.FailedIds);
            var skip = Assert.Single(result.SkippedMedia);
            Assert.Equal("a", skip.Id);
            Assert.Equal(Path.Combine(_dir, "..", Path.GetFileName(outside)), skip.Path);
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
        Assert.Empty(result.SkippedMedia);
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_MediaAlreadyMissing_RowStillDeleted()
    {
        Save("a");
        File.Delete(_repo.Load("a")!.MediaPath!);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.SkippedMedia);
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
        Assert.Empty(result.SkippedMedia);
        Assert.Null(_repo.Load("a"));
        Assert.False(File.Exists(aside), "the retry must reclaim the stale aside");
        Assert.False(File.Exists(media));
    }

    [Fact]
    public void DeleteMeetings_AsideCleanupBlocked_RowDeleted_SurvivorReported()
    {
        // Row delete succeeds but the aside unlink is blocked (AV/indexer lock): after the
        // brief retries the row must still be deleted and the leftover bytes at
        // <media>.deleting must be reported like any skipped survivor — silence here would
        // contradict the BulkDeleteResult contract (Domain.cs).
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        var aside = media + ".deleting";
        File.Move(media, aside);
        using (var hold = new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _repo.DeleteMeetings(new[] { "a" });

            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Empty(result.FailedIds);
            var skip = Assert.Single(result.SkippedMedia);
            Assert.Equal("a", skip.Id);
            Assert.Equal(aside, skip.Path);
            Assert.Null(_repo.Load("a"));
            Assert.True(File.Exists(aside));
        }
        File.Delete(aside);
    }

    // --- review round 6 (stale aside vs failed row delete) ---

    // Same crash state as above, but the retry's row delete fails too: the stale aside
    // must be RESTORED to the media path — row kept, bytes preserved, meeting retryable.
    // The old reclaim deleted the aside up front, so a row-delete failure after it
    // destroyed the recording's only copy while the row survived.
    [Fact]
    public void DeleteMeetings_StaleAsideAfterCrash_RowDeleteFails_AsideRestored()
    {
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        var aside = media + ".deleting";
        File.Move(media, aside);
        BlockRowDeletes();

        var failed = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(failed.DeletedIds);
        Assert.True(failed.FailedIds.ContainsKey("a"));
        Assert.Empty(failed.SkippedMedia);
        Assert.NotNull(_repo.Load("a"));
        Assert.True(File.Exists(media), "a failed row delete must move the stale aside back to the media path");
        Assert.False(File.Exists(aside), "the restore must not leave the aside behind");

        // The block lifts: the retry now sees a normal file and deletes cleanly.
        UnblockRowDeletes();
        var retry = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, retry.DeletedIds.ToArray());
        Assert.Empty(retry.SkippedMedia);
        Assert.Null(_repo.Load("a"));
        Assert.False(File.Exists(media));
        Assert.False(File.Exists(aside));
    }

    [Theory]
    [InlineData(MeetingStatus.Failed)]
    [InlineData(MeetingStatus.Processing)]
    public void DeleteMeetings_NonActiveStatuses_Deletable(MeetingStatus status)
    {
        Save("a", status: status);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.SkippedMedia);
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
            Assert.Empty(first.SkippedMedia);
        }

        var retry = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, retry.DeletedIds.ToArray());
        Assert.Empty(retry.SkippedMedia);
        Assert.False(File.Exists(media));
        Assert.False(File.Exists(media + ".deleting"), "the successful retry must not leave an aside file behind");
        Assert.Null(_repo.Load("a"));
    }

    // --- review round 2 (rename-aside ordering) ---

    [Fact]
    public void DeleteMeetings_RowDeleteFails_FileRestored_RowKept()
    {
        // Force the row delete to fail AFTER the aside rename (the established trigger).
        // The renamed recording must be put back so a failed row delete cannot lose the
        // audio.
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        BlockRowDeletes();

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.Empty(result.SkippedMedia);
        Assert.NotNull(_repo.Load("a"));
        Assert.True(File.Exists(media), "a failed row delete must restore the renamed recording");
        Assert.False(File.Exists(media + ".deleting"), "a restored delete must not leave an aside file behind");
    }

    // --- screen-video tracks (media_tracks) ---

    private string NewTrackFile(string meetingId, string stamp)
    {
        var path = Path.Combine(_dir, meetingId + "-screen-" + stamp + ".mp4");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        return path;
    }

    private void AddTrack(string meetingId, string path, long offsetMs = 0) =>
        _repo.AddMediaTrack(new MediaTrack("trk-" + Guid.NewGuid().ToString("N")[..10], meetingId, "screen", path, "video/mp4", offsetMs, 1000, DateTimeOffset.UtcNow.ToString("o")));

    [Fact]
    public void MediaTracks_AddListRoundTrip_OrderedByOffset()
    {
        AddTrack("a", NewTrackFile("a", "20260101010101"), 5000);
        AddTrack("a", NewTrackFile("a", "20260101010102"), 0);
        AddTrack("b", NewTrackFile("b", "20260101010103"), 0);

        var a = _repo.ListMediaTracks("a");
        Assert.Equal(2, a.Count);
        Assert.Equal(0, a[0].StartOffsetMs);
        Assert.Equal(5000, a[1].StartOffsetMs);
        Assert.All(a, t => Assert.Equal("screen", t.Kind));
        Assert.Single(_repo.ListMediaTracks("b"));
        Assert.Empty(_repo.ListMediaTracks("nope"));
    }

    [Fact]
    public void DeleteMeetings_RemovesTrackFiles_AndRows()
    {
        Save("a");
        var track1 = NewTrackFile("a", "20260101010101");
        var track2 = NewTrackFile("a", "20260101010202");
        AddTrack("a", track1);
        AddTrack("a", track2, 2000);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.SkippedMedia);
        Assert.False(File.Exists(track1));
        Assert.False(File.Exists(track2));
        Assert.False(File.Exists(track1 + ".deleting"));
        Assert.Empty(_repo.ListMediaTracks("a"));
        Assert.Null(_repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_UnmanagedTrackPath_RowDeleted_Reported()
    {
        Save("a");
        var foreign = Path.Combine(Path.GetTempPath(), "mv-foreign-track-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(foreign, new byte[] { 7 });
        try
        {
            AddTrack("a", foreign);

            var result = _repo.DeleteMeetings(new[] { "a" });

            Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
            Assert.Contains(result.SkippedMedia, s => s.Id == "a" && s.Path == foreign);
            Assert.True(File.Exists(foreign), "an unmanaged track must be left in place");
            Assert.Null(_repo.Load("a"));
        }
        finally { try { File.Delete(foreign); } catch { } }
    }

    [Fact]
    public void DeleteMeetings_LockedTrack_RowKept_EverythingRestored()
    {
        // The track fails to rename while the audio was already moved aside: the row must
        // be kept and BOTH files put back to their original paths.
        Save("a");
        var media = _repo.Load("a")!.MediaPath!;
        var track = NewTrackFile("a", "20260101010101");
        AddTrack("a", track);
        using var held = new FileStream(track, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.Empty(result.DeletedIds);
        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.Empty(result.SkippedMedia);
        Assert.NotNull(_repo.Load("a"));
        Assert.True(File.Exists(media), "the audio must be restored when a track rename fails");
        Assert.False(File.Exists(media + ".deleting"));
        Assert.True(File.Exists(track));
    }

    [Fact]
    public void DeleteMeetings_RowDeleteFails_TrackRestoredToo()
    {
        Save("a");
        var track = NewTrackFile("a", "20260101010101");
        AddTrack("a", track);
        BlockRowDeletes();

        var result = _repo.DeleteMeetings(new[] { "a" });

        Assert.True(result.FailedIds.ContainsKey("a"));
        Assert.True(File.Exists(track), "a failed row delete must restore the staged track");
        Assert.False(File.Exists(track + ".deleting"));
        Assert.NotNull(_repo.Load("a"));
        Assert.NotEmpty(_repo.ListMediaTracks("a"));
    }

    // Forced row-delete failure: a BEFORE DELETE trigger on meetings aborts the DELETE
    // deterministically and instantly (a competing write lock would only fail after the
    // 30 s busy timeout). Lives on the schema, so the repo's own connection sees it.
    private void BlockRowDeletes()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = "CREATE TRIGGER block_del BEFORE DELETE ON meetings BEGIN SELECT RAISE(ABORT, 'forced row-delete failure'); END;";
        c.ExecuteNonQuery();
    }

    private void UnblockRowDeletes()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = "DROP TRIGGER block_del;";
        c.ExecuteNonQuery();
    }
}
