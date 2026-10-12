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
}
