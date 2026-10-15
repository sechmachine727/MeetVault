using System.Text.Json;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public sealed class BackendModelsTests
{
    // Realistic payloads, copied from the backend's documented shapes.
    private const string StatusJson = """
    {"status":"processing","steps":{"transcribe":"succeeded","diarize":"running"},
     "progress":0.5,"active_job_id":"j-1","active_transcript_id":"t-1",
     "fresh_kinds":[],"stale_kinds":["summary"],"mock":true,"duration_ms":1834000}
    """;

    [Fact]
    public void Status_Deserializes_SnakeCase_Fields()
    {
        var status = JsonSerializer.Deserialize(StatusJson, BackendJsonContext.Default.BackendStatus)!;
        Assert.Equal("processing", status.Status);
        Assert.Equal("succeeded", status.Steps["transcribe"]);
        Assert.Equal("running", status.Steps["diarize"]);
        Assert.Equal(0.5, status.Progress);
        Assert.Equal("j-1", status.ActiveJobId);
        Assert.True(status.Mock);
        Assert.Single(status.StaleKinds);
        Assert.Equal(1834000, status.DurationMs);
    }

    [Fact]
    public void MediaAsset_Pins_Sha256_JsonName()
    {
        const string json = """
        {"id":"a1","meeting_id":"m1","kind":"audio","role":"primary","original_name":"m.m4a",
         "content_type":"audio/mp4","size_bytes":10,"sha256":"abc","storage_key":"k/1","duration_ms":5,
         "start_offset_ms":0,"sample_rate":48000,"channels":1,"probe":{},"created_at":"2026-10-12T00:00:00Z"}
        """;
        var asset = JsonSerializer.Deserialize(json, BackendJsonContext.Default.BackendMediaAsset)!;
        Assert.Equal("abc", asset.Sha256);
        Assert.Equal("audio", asset.Kind);
    }

    [Fact]
    public void MeetingCreateBody_Serializes_SnakeCase_AndOmitsNulls()
    {
        var body = new BackendMeetingCreateBody("Sync", "mv-1", "meetvault", null,
            "2026-10-12T02:30:00.0000000+00:00", null, 60000);
        var json = JsonSerializer.Serialize(body, BackendJsonContext.Default.BackendMeetingCreateBody);
        Assert.Contains("\"external_id\":\"mv-1\"", json);
        Assert.Contains("\"source\":\"meetvault\"", json);
        Assert.Contains("\"duration_ms\":60000", json);
        Assert.DoesNotContain("language", json);
        Assert.DoesNotContain("ended_at", json);
    }

    [Fact]
    public void ProcessBody_Serializes_Steps_And_Force()
    {
        var json = JsonSerializer.Serialize(new BackendProcessBody(new List<string> { "analyze" }, null, true),
            BackendJsonContext.Default.BackendProcessBody);
        Assert.Equal("{\"steps\":[\"analyze\"],\"force\":true}", json);
    }

    [Fact]
    public void Meeting_ListFields_Bind_HasAnalysis_And_LatestJob()
    {
        const string json = """
        {"id":"m1","title":"M","status":"ready","language":"en","source":"meetvault","external_id":null,
         "started_at":null,"ended_at":null,"duration_ms":1,"deleted_at":null,"created_at":null,"updated_at":null,
         "has_analysis":true,"latest_job_type":"analyze","latest_job_state":"succeeded"}
        """;
        var meeting = JsonSerializer.Deserialize(json, BackendJsonContext.Default.BackendMeeting)!;
        Assert.True(meeting.HasAnalysis);
        Assert.Equal("analyze", meeting.LatestJobType);
        Assert.Equal("succeeded", meeting.LatestJobState);
    }

    [Fact]
    public void MeetingDetail_And_Status_Carry_New_Fields()
    {
        const string detailJson = """
        {"id":"m1","title":"M","status":"ready","language":"en","source":"meetvault","external_id":null,
         "started_at":null,"ended_at":null,"duration_ms":1,"deleted_at":null,"created_at":null,"updated_at":null,
         "media_count":1,"transcript_count":1,"active_transcript_id":"t1","speaker_count":2,
         "has_analysis":true,"is_mock":false,"latest_job_type":"analyze","latest_job_state":"succeeded"}
        """;
        var detail = JsonSerializer.Deserialize(detailJson, BackendJsonContext.Default.BackendMeetingDetail)!;
        Assert.True(detail.HasAnalysis);
        Assert.Equal("analyze", detail.LatestJobType);
        Assert.Equal("succeeded", detail.LatestJobState);
        Assert.False(detail.IsMock);

        const string statusJson = """
        {"status":"failed","steps":{},"progress":0.0,"active_job_id":null,"active_transcript_id":null,
         "fresh_kinds":[],"stale_kinds":[],"mock":false,"duration_ms":0,
         "error":"boom","active_job_started_at":"2026-10-14T00:00:00Z"}
        """;
        var status = JsonSerializer.Deserialize(statusJson, BackendJsonContext.Default.BackendStatus)!;
        Assert.Equal("boom", status.Error);
        Assert.Equal("2026-10-14T00:00:00Z", status.ActiveJobStartedAt);
        // Older payloads without the new fields must still bind (defaults stay null).
        Assert.Null(JsonSerializer.Deserialize(StatusJson, BackendJsonContext.Default.BackendStatus)!.Error);
    }
}
