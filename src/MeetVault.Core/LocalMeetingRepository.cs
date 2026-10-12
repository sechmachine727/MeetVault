using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace MeetVault.Core;

// ponytail: raw SQL over EF; single-writer file DB needs no ORM.
public sealed class LocalMeetingRepository : IMeetingStore, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _mediaRoot;
    private readonly bool _sweepStagedDeletions;

    public LocalMeetingRepository(string path, string? mediaRoot = null)
    {
        _mediaRoot = Path.TrimEndingDirectorySeparator(mediaRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault", "media"));
        // Only an explicitly passed media root may be swept: a db-only construction (many
        // tests) would otherwise sweep the REAL media directory against a database that
        // knows nothing about its rows and could delete the only copy of a recording
        // (review r10).
        _sweepStagedDeletions = mediaRoot is not null;
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        AppLog.Info("repo: opened " + path);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meetings(id TEXT PRIMARY KEY, title TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT NULL, duration_ms INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL, media_path TEXT NULL, media_mime TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS speakers(meeting_id TEXT NOT NULL, speaker_id TEXT NOT NULL, display_name TEXT NOT NULL, gender TEXT NOT NULL DEFAULT 'Unknown', gender_index INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(meeting_id, speaker_id));
            CREATE TABLE IF NOT EXISTS transcript_segments(id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, speaker_id TEXT NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, text TEXT NOT NULL, is_final INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS gemini_jobs(id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, state TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL, result_paths TEXT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS media_tracks(id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, kind TEXT NOT NULL, path TEXT NOT NULL, mime TEXT NOT NULL DEFAULT 'video/mp4', start_offset_ms INTEGER NOT NULL DEFAULT 0, duration_ms INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_media_tracks_meeting ON media_tracks(meeting_id);
            """;
        cmd.ExecuteNonQuery();
        // Existing databases predate the language column; CREATE TABLE IF NOT EXISTS never
        // adds it, so migrate once per open.
        EnsureColumn("meetings", "language", "language TEXT NOT NULL DEFAULT ''");
        // A recording staged by a partially-failed batch delete (<file>.deleting) has no
        // row pointing at it anymore; without a sweep it leaks on disk silently and a
        // later delete of that id reports "not found" (review r7).
        if (_sweepStagedDeletions) SweepStagedDeletions();
    }

    private void SweepStagedDeletions()
    {
        try
        {
            if (!Directory.Exists(_mediaRoot)) return;
            // A row that still points at the original path means the crash happened in the
            // rename→row-delete window: the aside is the ONLY copy, so it is RESTORED, never
            // swept (review r8). Only asides no row references are collected.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT media_path FROM meetings WHERE media_path IS NOT NULL UNION SELECT path FROM media_tracks";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    try { referenced.Add(Path.GetFullPath(r.GetString(0))); } catch { }
                }
            }
            bool anyRows;
            using (var cnt = _db.CreateCommand())
            {
                cnt.CommandText = "SELECT (SELECT COUNT(*) FROM meetings) + (SELECT COUNT(*) FROM media_tracks)";
                anyRows = Convert.ToInt64(cnt.ExecuteScalar()) > 0;
            }
            // A delete marker means an empty db is a committed delete, not db loss (r13).
            var deletePending = false;
            try { deletePending = !string.IsNullOrEmpty(GetSetting("repo.deletePending")); } catch { }
            foreach (var path in Directory.EnumerateFiles(_mediaRoot, "*.deleting"))
            {
                string orig;
                try { orig = Path.GetFullPath(path[..^".deleting".Length]); } catch { continue; }
                if (referenced.Contains(orig))
                {
                    try
                    {
                        if (!File.Exists(orig)) { File.Move(path, orig); AppLog.Info("repo: restored staged deletion " + path + " -> " + orig); }
                        else AppLog.Warn("repo: staged deletion left in place (both files exist): " + path);
                    }
                    catch (Exception ex) { AppLog.Warn("repo: could not restore staged deletion: " + path + " :: " + ex.Message); }
                    continue;
                }
                // No row references it. Recover only when the whole database is empty (the
                // lost/reset case this branch was written for): with rows present, no-row +
                // no-original is the committed-delete window (crash or transient lock before
                // the unlink), and resurrecting the file would make a user-confirmed delete
                // un-doable and un-sweepable (review r12). Otherwise reclaim it; a duplicate
                // (original present) is always deleted.
                try
                {
                    if (!anyRows && !deletePending && !File.Exists(orig)) { File.Move(path, orig); AppLog.Info("repo: recovered orphaned staged deletion " + path + " -> " + orig); }
                    else { File.Delete(path); AppLog.Info("repo: swept staged deletion " + path); }
                }
                catch (Exception ex) { AppLog.Warn("repo: staged deletion still locked: " + path + " :: " + ex.Message); }
            }
            // The window is resolved; a stale marker must not block a future genuine
            // recovery (review r13).
            if (deletePending) { try { SetSetting("repo.deletePending", ""); } catch { } }
        }
        catch (Exception ex) { AppLog.Warn("repo: staged-deletion sweep failed: " + ex.Message); }
    }

    // CREATE TABLE IF NOT EXISTS never adds columns to an existing database; check the
    // live schema and ALTER once per missing column. Idempotent and safe on every open.
    private void EnsureColumn(string table, string column, string ddl)
    {
        using (var check = _db.CreateCommand())
        {
            check.CommandText = "pragma table_info(" + table + ")";
            using var reader = check.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = _db.CreateCommand();
        alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + ddl;
        alter.ExecuteNonQuery();
    }

    public void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments)
    {
        AppLog.Debug($"repo: save meeting={meeting.Id} status={meeting.Status} speakers={speakers.Count} segments={segments.Count} media={meeting.MediaPath}");
        using var tx = _db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using (var up = _db.CreateCommand())
        {
            up.CommandText = "INSERT INTO meetings(id,title,started_at,ended_at,duration_ms,status,media_path,media_mime,created_at,updated_at) VALUES($id,$t,$s,$e,$d,$st,$mp,$mm,$now,$now) ON CONFLICT(id) DO UPDATE SET title=$t,ended_at=$e,duration_ms=$d,status=$st,media_path=$mp,media_mime=$mm,updated_at=$now";
            up.Parameters.AddWithValue("$id", meeting.Id); up.Parameters.AddWithValue("$t", meeting.Title);
            up.Parameters.AddWithValue("$s", meeting.StartedAt.ToUniversalTime().ToString("o")); up.Parameters.AddWithValue("$e", (object?)(meeting.EndedAt?.ToUniversalTime().ToString("o")) ?? DBNull.Value); // UTC: lexicographic TEXT order == chronological
            up.Parameters.AddWithValue("$d", meeting.EndedAt.HasValue ? (long)(meeting.EndedAt.Value - meeting.StartedAt).TotalMilliseconds : 0);
            up.Parameters.AddWithValue("$st", meeting.Status.ToString()); up.Parameters.AddWithValue("$mp", (object?)meeting.MediaPath ?? DBNull.Value);
            up.Parameters.AddWithValue("$mm", (object?)meeting.MediaMime ?? DBNull.Value); up.Parameters.AddWithValue("$now", now);
            up.ExecuteNonQuery();
        }
        foreach (var sp in speakers)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO speakers(meeting_id,speaker_id,display_name) VALUES($m,$s,$d) ON CONFLICT(meeting_id,speaker_id) DO UPDATE SET display_name=$d";
            c.Parameters.AddWithValue("$m", meeting.Id); c.Parameters.AddWithValue("$s", sp.Id); c.Parameters.AddWithValue("$d", sp.DisplayName);
            c.ExecuteNonQuery();
        }
        using (var del = _db.CreateCommand()) { del.CommandText = "DELETE FROM transcript_segments WHERE meeting_id=$m"; del.Parameters.AddWithValue("$m", meeting.Id); del.ExecuteNonQuery(); }
        foreach (var sg in segments)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO transcript_segments(id,meeting_id,speaker_id,start_ms,end_ms,text,is_final,created_at,updated_at) VALUES($id,$m,$s,$a,$b,$t,$f,$now,$now)";
            c.Parameters.AddWithValue("$id", sg.Id); c.Parameters.AddWithValue("$m", meeting.Id); c.Parameters.AddWithValue("$s", sg.SpeakerId);
            c.Parameters.AddWithValue("$a", sg.StartMs); c.Parameters.AddWithValue("$b", sg.EndMs); c.Parameters.AddWithValue("$t", sg.Text);
            c.Parameters.AddWithValue("$f", sg.IsFinal ? 1 : 0); c.Parameters.AddWithValue("$now", now);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        AppLog.Debug("repo: save committed " + meeting.Id);
    }

    // The meeting's active transcription language (see the Meeting record note). Kept out
    // of Save's upsert on purpose: checkpoints must never clobber a value only this
    // setter owns.
    public void SetMeetingLanguage(string meetingId, string language)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE meetings SET language=$l, updated_at=$u WHERE id=$id";
        c.Parameters.AddWithValue("$l", language);
        c.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("o"));
        c.Parameters.AddWithValue("$id", meetingId);
        c.ExecuteNonQuery();
    }

    public int CountSegments(string meetingId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM transcript_segments WHERE meeting_id=$m";
        c.Parameters.AddWithValue("$m", meetingId);
        return Convert.ToInt32(c.ExecuteScalar());
    }

    public Meeting? Load(string meetingId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime,language FROM meetings WHERE id=$m";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new Meeting(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), Enum.TryParse<MeetingStatus>(r.GetString(4), out var st) ? st : MeetingStatus.Failed, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? "" : r.GetString(7));
    }

    public void RenameSpeaker(string meetingId, string speakerId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", "displayName");
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE speakers SET display_name=$d WHERE meeting_id=$m AND speaker_id=$s";
        c.Parameters.AddWithValue("$d", displayName); c.Parameters.AddWithValue("$m", meetingId); c.Parameters.AddWithValue("$s", speakerId);
        c.ExecuteNonQuery();
    }

    // Deletes the meeting and everything that belongs to it, in one transaction.
    // Returns the recording path (if any) so the caller can remove the audio file.
    // Refuses active or resumable recordings (Recording/Paused) with the same message
    // the bulk path reports, so both callers share one rule: a draft is deleted by
    // resuming and stopping it in Live (Stop, then Stop and save), never through a delete path.
    public string? DeleteMeeting(string meetingId)
    {
        string? media = null;
        using (var q = _db.CreateCommand())
        {
            q.CommandText = "SELECT media_path,status FROM meetings WHERE id=$m";
            q.Parameters.AddWithValue("$m", meetingId);
            using var r = q.ExecuteReader();
            if (r.Read())
            {
                if (!r.IsDBNull(1) && Enum.TryParse<MeetingStatus>(r.GetString(1), out var st)
                    && st is MeetingStatus.Recording or MeetingStatus.Paused)
                    throw new InvalidOperationException("active or resumable recording");
                media = r.IsDBNull(0) ? null : r.GetString(0);
            }
        }
        using var tx = _db.BeginTransaction();
        foreach (var table in new[] { "transcript_segments", "speakers", "gemini_jobs", "media_tracks", "meetings" })
        {
            using var d = _db.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "DELETE FROM " + table + " WHERE " + (table == "meetings" ? "id=$m" : "meeting_id=$m");
            d.Parameters.AddWithValue("$m", meetingId);
            d.ExecuteNonQuery();
        }
        tx.Commit();
        AppLog.Info("repo: deleted meeting " + meetingId + (media is null ? "" : " media=" + media));
        return media;
    }

    // Deletes many meetings, reporting per-item failures instead of aborting the batch.
    // Per item the recording is renamed ASIDE (media + ".deleting") BEFORE the DB rows,
    // mirroring the single-row path: a failed rename keeps the row (retryable) and a
    // failed row delete restores the file. A crash between the rename and the row delete
    // leaves a *.deleting file beside a row still pointing at the original name — the
    // bytes survive and the row stays retryable, and the next retry reclaims the stale
    // aside: it is ADOPTED as this attempt's aside (never deleted up front — a row
    // delete failing after that deletion would destroy the recording's only copy while
    // the row survives), then unlinked on success or moved back on failure. Media
    // outside the managed directory is never touched but never blocks the row either
    // (export, user copy, malformed path), exactly like the single-row path; such a row
    // is still deleted and the surviving file — when it still exists — is reported in
    // SkippedMedia, because the confirmation promised the recording would be removed.
    // A recording whose cleanup fails after the row delete is retried briefly and, if
    // the bytes remain, reported in SkippedMedia too. Screen-video tracks recorded with
    // the meeting — every file in media_tracks — follow the identical protocol, one by
    // one. Active or resumable recordings are refused.
    public BulkDeleteResult DeleteMeetings(IReadOnlyCollection<string> meetingIds)
    {
        var deleted = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<(string Id, string Path)>();
        // A committed delete that crashes before unlinking its asides must be distinguishable
        // from a lost database on the next open: this marker (cleared at batch end) tells
        // the sweep that an empty db is due to a delete, not db loss (review r13).
        try { SetSetting("repo.deletePending", "1"); } catch { }
        foreach (var id in meetingIds.Distinct(StringComparer.Ordinal))
        {
            // Junk entries are skipped: null/empty can never name a meeting and the
            // failure map cannot key them.
            if (string.IsNullOrEmpty(id)) continue;
            try
            {
                var meeting = Load(id);
                if (meeting is null) { failed[id] = "not found"; continue; }
                if (meeting.Status is MeetingStatus.Recording or MeetingStatus.Paused)
                { failed[id] = "active or resumable recording"; continue; }
                // Every file that must disappear with this meeting — the audio recording and
                // each screen-video track — goes through the same protocol below.
                var candidates = new List<string>();
                if (meeting.MediaPath is { Length: > 0 } m) candidates.Add(m);
                foreach (var track in ListMediaTracks(id)) candidates.Add(track.Path);
                // A kept working WAV (<recording>.wav beside the .m4a) must go with the
                // meeting too: otherwise a failed unlink leaves an un-sweepable orphan and a
                // later delete reports success while the bytes survive (review r12).
                foreach (var p in candidates.Where(p => p.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var wav = Path.ChangeExtension(p, ".wav");
                    if (File.Exists(wav)) candidates.Add(wav);
                }
                var staged = new List<(string Orig, string Aside, bool Move)>();
                var unmanaged = new List<string>();
                foreach (var p in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsManagedMedia(p))
                    {
                        // Never touch a foreign path; the row still goes, so an
                        // unmanaged-media meeting is not permanently undeletable. A
                        // survivor that still EXISTS is reported in SkippedMedia (the
                        // confirm dialog promised the recording would be removed) — only
                        // once the row is actually deleted below, so a failed row never
                        // double-reports. A path whose file is already gone leaves nothing
                        // behind and is not reported.
                        if (File.Exists(p))
                        {
                            AppLog.Warn("repo: bulk delete leaving unmanaged media in place: " + p + " (meeting " + id + ")");
                            unmanaged.Add(p);
                        }
                    }
                    else if (File.Exists(p))
                    {
                        // Rename aside first (same order as the single-row delete): the row
                        // must never be deleted while its bytes still sit at the original
                        // path. A failed rename keeps the row so the user can retry.
                        staged.Add((p, p + ".deleting", true));
                    }
                    else if (File.Exists(p + ".deleting"))
                    {
                        // Crash-left aside: ADOPT the stale aside as this attempt's aside —
                        // the success cleanup unlinks it and a failed row delete moves it
                        // back to the original path. (The old reclaim deleted it up front,
                        // so a row delete failing afterwards destroyed the only bytes.)
                        staged.Add((p, p + ".deleting", false));
                    }
                }
                var movedNow = new List<(string Orig, string Aside)>();
                try
                {
                    foreach (var s in staged)
                    {
                        if (!s.Move) continue;
                        File.Move(s.Orig, s.Aside, overwrite: true);
                        movedNow.Add((s.Orig, s.Aside));
                    }
                }
                catch (Exception ex)
                {
                    // A failed rename keeps the row (retryable) and puts back everything
                    // this attempt already moved.
                    foreach (var (orig, aside) in movedNow)
                    {
                        try { File.Move(aside, orig, overwrite: true); }
                        catch (Exception back) { AppLog.Warn("repo: bulk delete could not restore media " + aside + " :: " + back.Message); }
                    }
                    failed[id] = ex.Message;
                    continue;
                }
                try
                {
                    DeleteMeeting(id);
                }
                catch
                {
                    // Row delete failed after the renames: put every staged recording back
                    // (best effort) so the meeting stays retryable, then let the outer
                    // handler record the failure.
                    foreach (var s in staged)
                    {
                        try { File.Move(s.Aside, s.Orig, overwrite: true); }
                        catch (Exception back) { AppLog.Warn("repo: bulk delete could not restore media " + s.Aside + " :: " + back.Message); }
                    }
                    throw;
                }
                foreach (var s in staged)
                {
                    // The row is gone; a survivor at <path>.deleting is reported like any
                    // other skipped media — the confirmation promised the recording would
                    // be removed, and an unreported leftover contradicts the
                    // BulkDeleteResult contract (Domain.cs).
                    if (!TryDeleteAside(s.Aside)) skipped.Add((id, s.Aside));
                }
                deleted.Add(id);
                foreach (var p in unmanaged) skipped.Add((id, p));
            }
            catch (Exception ex) { failed[id] = ex.Message; }
        }
        // The batch is done; the crash window keeps the marker alive (review r13) — and so
        // does any staged aside that survived deletion: with the marker set, the next
        // open's sweep can still reclaim it instead of resurrecting it as an orphan
        // (review r19).
        var asideSurvived = skipped.Any(s => s.Path.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase));
        if (!asideSurvived)
        {
            try { SetSetting("repo.deletePending", ""); } catch { }
        }
        AppLog.Info($"repo: bulk delete {deleted.Count} deleted, {failed.Count} failed, {skipped.Count} media left in place");
        return new BulkDeleteResult(deleted, failed, skipped);
    }

    // AV/indexer locks on a just-renamed file are transient: retry briefly, then let the
    // caller report the survivor. A missing file counts as removed.
    private static bool TryDeleteAside(string aside, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try { File.Delete(aside); return true; }
            catch (Exception ex)
            {
                if (i >= attempts) { AppLog.Warn("repo: bulk delete could not remove media " + aside + " :: " + ex.Message); return false; }
                Thread.Sleep(150);
            }
        }
    }

    // Only recordings under the managed media directory may be removed by batch
    // delete. Anything else (exports, user copies, malformed rows) is refused so a
    // bad database record can never turn into deletion of an arbitrary path.
    // The check is deliberately lexical AND physical: the path must be fully
    // qualified and resolve under the configured root, and no component BELOW the
    // boundary may be a reparse point (symlink/junction) — a link below the root
    // could otherwise redirect a delete outside it. The ROOT itself may be a link:
    // that is the user's own media location (e.g. relocated to another drive with a
    // junction), so it is resolved to its final target and the file is judged under
    // that target — otherwise every recording on a relocated root would read as
    // unmanaged and deleting it would silently leave the audio behind. Rows still
    // cannot escape the boundary or pass through links below it.
    public bool IsManagedMedia(string path)
    {
        string full, root;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(_mediaRoot);
        }
        catch { return false; }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = full.Substring(root.Length + 1);

        var boundary = root;
        var candidate = full;
        try
        {
            var rootInfo = new DirectoryInfo(root);
            if (rootInfo.Exists && (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A reparse root that cannot be resolved refuses the delete: without a
                // boundary the file's real location is unknowable.
                var resolved = rootInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is null) return false;
                boundary = Path.TrimEndingDirectorySeparator(resolved.FullName);
                candidate = Path.Combine(boundary, relative);
            }
        }
        catch { return false; }   // resolve/attribute errors: refuse
        // A missing root is not a refusal (no file can exist under it; callers guard
        // File.Exists); an unreadable one is (HasReparsePoint refuses on errors).
        return !HasReparsePoint(candidate, boundary);
    }

    // True when any component strictly BELOW `boundary` is a reparse point. The
    // boundary itself is judged by the caller (it may be the user's own relocated root).
    private static bool HasReparsePoint(string full, string root)
    {
        var current = root;
        var rest = full.Substring(root.Length + 1);
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { return false; }        // nothing on disk to reach
            catch (DirectoryNotFoundException) { return false; }   // ditto
            catch { return true; }                                 // unreadable: refuse
        }
        return false;
    }


    public IReadOnlyList<Segment> LoadSegments(string meetingId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,speaker_id,start_ms,end_ms,text,is_final FROM transcript_segments WHERE meeting_id=$m ORDER BY start_ms,id";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        var list = new List<Segment>();
        while (r.Read()) list.Add(new Segment(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt32(5) != 0));
        return list;
    }

    public IReadOnlyList<Speaker> LoadSpeakers(string meetingId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT speaker_id,display_name FROM speakers WHERE meeting_id=$m ORDER BY speaker_id";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        var list = new List<Speaker>();
        while (r.Read()) list.Add(new Speaker(r.GetString(0), r.GetString(1)));
        return list;
    }

    public IReadOnlyList<Meeting> ListMeetings()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime,language FROM meetings ORDER BY started_at DESC";
        using var r = c.ExecuteReader();
        var list = new List<Meeting>();
        while (r.Read()) list.Add(ReadMeeting(r));
        return list;
    }

    public IReadOnlyList<Meeting> LoadUnfinished()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime,language FROM meetings WHERE status IN ('" + nameof(MeetingStatus.Recording) + "','" + nameof(MeetingStatus.Paused) + "') ORDER BY started_at DESC";
        using var r = c.ExecuteReader();
        var list = new List<Meeting>();
        while (r.Read()) list.Add(ReadMeeting(r));
        return list;
    }

    public void SaveJob(GeminiJob job)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO gemini_jobs(id,meeting_id,state,attempts,last_error,result_paths,updated_at) VALUES($id,$m,$s,$a,$e,$r,$n) ON CONFLICT(id) DO UPDATE SET state=$s,attempts=$a,last_error=$e,result_paths=$r,updated_at=$n";
        c.Parameters.AddWithValue("$id", job.Id); c.Parameters.AddWithValue("$m", job.MeetingId);
        c.Parameters.AddWithValue("$s", job.State.ToString()); c.Parameters.AddWithValue("$a", job.Attempts);
        c.Parameters.AddWithValue("$e", (object?)job.LastError ?? DBNull.Value); c.Parameters.AddWithValue("$r", (object?)job.ResultPath ?? DBNull.Value);
        c.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToString("o"));
        c.ExecuteNonQuery();
    }

    public GeminiJob? LoadJob(string meetingId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,state,attempts,last_error,result_paths FROM gemini_jobs WHERE meeting_id=$m ORDER BY updated_at DESC LIMIT 1";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new GeminiJob(r.GetString(0), meetingId, Enum.TryParse<GeminiJobState>(r.GetString(1), out var js) ? js : GeminiJobState.Failed, r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    // ponytail: unknown enum text falls back to Failed, never throws; surfaces in UI instead of breaking list/load.
    private static Meeting ReadMeeting(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), Enum.TryParse<MeetingStatus>(r.GetString(4), out var st) ? st : MeetingStatus.Failed, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? "" : r.GetString(7));


    public string? GetSetting(string key)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }

    public void DeleteSetting(string key)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM settings WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        c.ExecuteNonQuery();
    }

    public void AddMediaTrack(MediaTrack track)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO media_tracks(id,meeting_id,kind,path,mime,start_offset_ms,duration_ms,created_at) VALUES($id,$m,$k,$p,$mi,$o,$d,$c)";
        c.Parameters.AddWithValue("$id", track.Id);
        c.Parameters.AddWithValue("$m", track.MeetingId);
        c.Parameters.AddWithValue("$k", track.Kind);
        c.Parameters.AddWithValue("$p", track.Path);
        c.Parameters.AddWithValue("$mi", track.Mime);
        c.Parameters.AddWithValue("$o", track.StartOffsetMs);
        c.Parameters.AddWithValue("$d", track.DurationMs);
        c.Parameters.AddWithValue("$c", track.CreatedAt);
        c.ExecuteNonQuery();
    }

    public IReadOnlyList<MediaTrack> ListMediaTracks(string meetingId)
    {
        var list = new List<MediaTrack>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,meeting_id,kind,path,mime,start_offset_ms,duration_ms,created_at FROM media_tracks WHERE meeting_id=$m ORDER BY start_offset_ms, created_at";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new MediaTrack(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7)));
        return list;
    }

    public void SetSetting(string key, string value)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value);
        c.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}








