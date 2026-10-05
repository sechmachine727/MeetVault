using Microsoft.Data.Sqlite;

namespace MeetVault.Core;

// ponytail: raw SQL over EF; single-writer file DB needs no ORM.
public sealed class LocalMeetingRepository : IMeetingStore, IDisposable
{
    private readonly SqliteConnection _db;

    public LocalMeetingRepository(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
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
        using var tx = _db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using (var up = _db.CreateCommand())
        {
            up.CommandText = "INSERT INTO meetings(id,title,started_at,ended_at,duration_ms,status,media_path,media_mime,created_at,updated_at) VALUES($id,$t,$s,$e,$d,$st,$mp,$mm,$now,$now) ON CONFLICT(id) DO UPDATE SET title=$t,ended_at=$e,duration_ms=$d,status=$st,media_path=$mp,media_mime=$mm,updated_at=$now";
            up.Parameters.AddWithValue("$id", meeting.Id); up.Parameters.AddWithValue("$t", meeting.Title);
            up.Parameters.AddWithValue("$s", meeting.StartedAt.ToString("o")); up.Parameters.AddWithValue("$e", (object?)meeting.EndedAt?.ToString("o") ?? DBNull.Value);
            up.Parameters.AddWithValue("$d", meeting.EndedAt.HasValue ? (long)(meeting.EndedAt.Value - meeting.StartedAt).TotalMilliseconds : 0);
            up.Parameters.AddWithValue("$st", meeting.Status.ToString()); up.Parameters.AddWithValue("$mp", (object?)meeting.MediaPath ?? DBNull.Value);
            up.Parameters.AddWithValue("$mm", (object?)meeting.MediaMime ?? DBNull.Value); up.Parameters.AddWithValue("$now", now);
            up.ExecuteNonQuery();
        }
        foreach (var sp in speakers)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO speakers(meeting_id,speaker_id,display_name,gender,gender_index) VALUES($m,$s,$d,$g,$i) ON CONFLICT(meeting_id,speaker_id) DO UPDATE SET display_name=$d,gender=$g,gender_index=$i";
            c.Parameters.AddWithValue("$m", meeting.Id); c.Parameters.AddWithValue("$s", sp.Id); c.Parameters.AddWithValue("$d", sp.DisplayName);
            c.Parameters.AddWithValue("$g", sp.Gender.ToString()); c.Parameters.AddWithValue("$i", sp.GenderIndex);
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
        return new Meeting(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), Enum.Parse<MeetingStatus>(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    public void RenameSpeaker(string meetingId, string speakerId, string displayName)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE speakers SET display_name=$d WHERE meeting_id=$m AND speaker_id=$s";
        c.Parameters.AddWithValue("$d", displayName); c.Parameters.AddWithValue("$m", meetingId); c.Parameters.AddWithValue("$s", speakerId);
        c.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

