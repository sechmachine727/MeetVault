using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace MeetVault.Core;

// ponytail: raw SQL over EF; single-writer file DB needs no ORM.
public sealed class LocalMeetingRepository : IMeetingStore, IDisposable
{
    private readonly SqliteConnection _db;

    public LocalMeetingRepository(string path)
    {
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
            """;
        cmd.ExecuteNonQuery();
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
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime FROM meetings WHERE id=$m";
        c.Parameters.AddWithValue("$m", meetingId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new Meeting(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), Enum.TryParse<MeetingStatus>(r.GetString(4), out var st) ? st : MeetingStatus.Failed, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
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
    public string? DeleteMeeting(string meetingId)
    {
        string? media = null;
        using (var q = _db.CreateCommand())
        {
            q.CommandText = "SELECT media_path FROM meetings WHERE id=$m";
            q.Parameters.AddWithValue("$m", meetingId);
            media = q.ExecuteScalar() as string;
        }
        using var tx = _db.BeginTransaction();
        foreach (var table in new[] { "transcript_segments", "speakers", "gemini_jobs", "meetings" })
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
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime FROM meetings ORDER BY started_at DESC";
        using var r = c.ExecuteReader();
        var list = new List<Meeting>();
        while (r.Read()) list.Add(ReadMeeting(r));
        return list;
    }

    public IReadOnlyList<Meeting> LoadUnfinished()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,title,started_at,ended_at,status,media_path,media_mime FROM meetings WHERE status IN ('" + nameof(MeetingStatus.Recording) + "','" + nameof(MeetingStatus.Paused) + "') ORDER BY started_at DESC";
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
    private static Meeting ReadMeeting(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), Enum.TryParse<MeetingStatus>(r.GetString(4), out var st) ? st : MeetingStatus.Failed, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));


    public string? GetSetting(string key)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
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








