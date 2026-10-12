using MeetVault.Core;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public sealed class BackendSyncTests
{
    private static readonly string DetailJson =
        """{"id":"b1","title":"Sync","status":"uploaded","language":"en","source":"meetvault","external_id":"mv-1","started_at":null,"ended_at":null,"duration_ms":1,"deleted_at":null,"created_at":null,"updated_at":null,"media_count":0,"transcript_count":0,"active_transcript_id":null,"speaker_count":0,"has_analysis":false,"is_mock":false}""";
    private static readonly string TranscriptJson =
        """{"id":"t1","meeting_id":"b1","version":1,"source":"live_export","provider":"client","model":"","language":"en","is_mock":false,"is_active":true,"segment_count":1,"created_at":null,"segments":[],"duration_ms":5}""";
    private static readonly string ProcessJson =
        """{"meeting_id":"b1","steps":["transcribe"],"job_ids":["jg"],"enqueued":1}""";

    private static Meeting MeetingWithAudio(string audioPath) =>
        new("mv-1", "Sync", DateTimeOffset.Parse("2026-10-12T02:30:00Z"), DateTimeOffset.Parse("2026-10-12T02:31:00Z"),
            MeetingStatus.Ready, audioPath, "audio/mp4", "en");

    private static string TempAudio()
    {
        var path = Path.Combine(Path.GetTempPath(), "mvb-sync-" + Guid.NewGuid().ToString("n") + ".m4a");
        File.WriteAllText(path, "audio");
        return path;
    }

    [Fact]
    public async Task FirstSend_CallsEndpointsInOrder_MediaOnce()
    {
        var transport = new RecordingTransport();
        var seen = new List<string>();
        transport.Responder = request =>
        {
            seen.Add(request.Method + " " + request.Path);
            return request.Path switch
            {
                "/v1/meetings" => DetailJson,
                "/v1/meetings/b1/transcripts" => TranscriptJson,
                "/v1/meetings/b1/process" => ProcessJson,
                _ => "{}",
            };
        };
        var audio = TempAudio();
        var screen = TempAudio();
        try
        {
            var client = new BackendClient(transport);
            var sync = new BackendSync(client);
            var result = await sync.SendAsync(MeetingWithAudio(audio),
                new List<Segment> { new("s1", "spk_1", 0, 10, "hi", true) },
                new List<Speaker> { new("spk_1", "Speaker 1") },
                new List<MediaTrack> { new("mt1", "mv-1", "screen", screen, "video/mp4", 4200, 0, "t") },
                existingBackendId: null);

            Assert.Equal("b1", result.BackendId);
            Assert.Equal(new[]
            {
                "POST /v1/meetings",
                "POST /v1/meetings/b1/media",
                "POST /v1/meetings/b1/media",
                "POST /v1/meetings/b1/transcripts",
                "POST /v1/meetings/b1/process",
            }, seen);
            Assert.Contains("transcribe", transport.Requests[4].JsonBody);
            Assert.DoesNotContain("force\":true", transport.Requests[4].JsonBody!);
        }
        finally { File.Delete(audio); File.Delete(screen); }
    }

    [Fact]
    public async Task Resend_SkipsMedia_AndForcesAnalyzeOnly()
    {
        var transport = new RecordingTransport();
        transport.Responder = request => request.Path switch
        {
            "/v1/meetings/b1/transcripts" => TranscriptJson,
            "/v1/meetings/b1/process" => ProcessJson,
            _ => "{}",
        };
        var client = new BackendClient(transport);
        var sync = new BackendSync(client);
        await sync.SendAsync(MeetingWithAudio("C:/nonexistent/a.m4a"),
            new List<Segment> { new("s1", "", 0, 10, "hi", true) }, new List<Speaker>(),
            new List<MediaTrack>(), existingBackendId: "b1");
        var paths = transport.Requests.Select(r => r.Path).ToList();
        Assert.Equal(new[] { "/v1/meetings/b1/transcripts", "/v1/meetings/b1/process" }, paths);
        Assert.Contains("\"steps\":[\"analyze\"]", transport.Requests[1].JsonBody);
        Assert.Contains("\"force\":true", transport.Requests[1].JsonBody);
    }

    [Fact]
    public async Task WaitForTerminal_TicksUntilReady()
    {
        var transport = new RecordingTransport();
        var states = new Queue<string>(new[] { "processing", "processing", "ready" });
        transport.Responder = _ => "{\"status\":\"" + states.Dequeue() + "\",\"steps\":{},\"progress\":0.5,\"active_job_id\":null,\"active_transcript_id\":null,\"fresh_kinds\":[],\"stale_kinds\":[],\"mock\":true,\"duration_ms\":10}";
        var client = new BackendClient(transport);
        var sync = new BackendSync(client);
        var ticks = new List<string>();
        var final = await sync.WaitForTerminalAsync("b1", s => ticks.Add(s.Status), (_, _) => Task.CompletedTask);
        Assert.Equal("ready", final.Status);
        Assert.Equal(new[] { "processing", "processing", "ready" }, ticks);
    }
}
