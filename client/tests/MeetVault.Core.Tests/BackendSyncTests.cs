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
    // Media upload response: probe says 90 s, deliberately different from MeetingWithAudio's 60 s
    // EndedAt-StartedAt span — R1 asserts the transcript targets the MEETING duration, not the probe.
    private static readonly string MediaUploadJson =
        """{"asset_id":"a1","size_bytes":1,"sha256":"s","duration_ms":90000,"sample_rate":null,"channels":null,"is_video":false,"probe_error":null}""";

    private static Meeting MeetingWithAudio(string audioPath) =>
        new("mv-1", "Sync", DateTimeOffset.Parse("2026-10-12T02:30:00Z"), DateTimeOffset.Parse("2026-10-12T02:31:00Z"),
            MeetingStatus.Ready, audioPath, "audio/mp4", "en");

    // Zero-duration meeting: the server then falls back to the audio probe for align=auto.
    private static Meeting MeetingNoDuration(string audioPath) =>
        new("mv-1", "Sync", DateTimeOffset.Parse("2026-10-12T02:30:00Z"), null,
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
                "/v1/meetings/b1/media" => MediaUploadJson,
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
            // R1: the transcript targets the meeting duration (60 s), NOT the media probe (90 s).
            Assert.Contains("\"end_ms\":60000", transport.Requests[3].JsonBody);
            Assert.DoesNotContain("\"end_ms\":90000", transport.Requests[3].JsonBody);
            Assert.Contains("transcribe", transport.Requests[4].JsonBody);
            Assert.DoesNotContain("force\":true", transport.Requests[4].JsonBody!);
            // Pin the full first-send process body and every media upload query (kind/role, offset for video).
            Assert.Equal("{\"steps\":[\"transcribe\",\"diarize\",\"align\",\"analyze\"],\"force\":false}", transport.Requests[4].JsonBody);
            Assert.Equal("audio", transport.Requests[1].Query!["kind"]);
            Assert.Equal("primary", transport.Requests[1].Query!["role"]);
            Assert.False(transport.Requests[1].Query!.ContainsKey("start_offset_ms"));
            Assert.Equal("video", transport.Requests[2].Query!["kind"]);
            Assert.Equal("primary", transport.Requests[2].Query!["role"]);
            Assert.Equal("4200", transport.Requests[2].Query!["start_offset_ms"]);
        }
        finally { File.Delete(audio); File.Delete(screen); }
    }

    [Fact]
    public async Task Resend_WithDuration_SkipsMediaFetch_AndForcesAnalyzeOnly()
    {
        var transport = new RecordingTransport();
        transport.Responder = request => request.Path switch
        {
            "/v1/meetings/b1/media" => "{\"items\":[],\"total\":0,\"limit\":50,\"offset\":0}",
            "/v1/meetings/b1/transcripts" => TranscriptJson,
            "/v1/meetings/b1/process" => ProcessJson,
            _ => "{}",
        };
        var client = new BackendClient(transport);
        var sync = new BackendSync(client);
        await sync.SendAsync(MeetingWithAudio("C:/nonexistent/a.m4a"),
            new List<Segment> { new("s1", "", 0, 10, "hi", true) }, new List<Speaker>(),
            new List<MediaTrack>(), existingBackendId: "b1");
        // R1: the meeting carries a duration, so NO media GET — transcripts POST then process POST.
        var paths = transport.Requests.Select(r => r.Path).ToList();
        Assert.Equal(new[] { "/v1/meetings/b1/transcripts", "/v1/meetings/b1/process" }, paths);
        Assert.DoesNotContain(transport.Requests, r => r.Path.Contains("/media"));
        Assert.Contains("\"end_ms\":60000", transport.Requests[0].JsonBody);
        Assert.Contains("\"steps\":[\"analyze\"]", transport.Requests[1].JsonBody);
        Assert.Contains("\"force\":true", transport.Requests[1].JsonBody);
    }

    [Fact]
    public async Task Send_WithoutSegments_SkipsTranscript_ButStillProcesses()
    {
        var transport = new RecordingTransport();
        var seen = new List<string>();
        transport.Responder = request =>
        {
            seen.Add(request.Method + " " + request.Path);
            return request.Path switch
            {
                "/v1/meetings" => DetailJson,
                "/v1/meetings/b1/process" => ProcessJson,
                _ => "{}",
            };
        };
        var audio = TempAudio();
        try
        {
            var client = new BackendClient(transport);
            var sync = new BackendSync(client);
            var result = await sync.SendAsync(MeetingWithAudio(audio),
                new List<Segment>(), new List<Speaker>(), new List<MediaTrack>(), existingBackendId: null);
            Assert.DoesNotContain(seen, s => s.Contains("/transcripts"));
            Assert.Contains("POST /v1/meetings/b1/process", seen);
            Assert.Equal(0, result.TranscriptVersion);
        }
        finally { File.Delete(audio); }
    }

    [Fact]
    public async Task Send_AllBlankSegments_SkipsTranscript_ButStillProcesses()
    {
        var transport = new RecordingTransport();
        var seen = new List<string>();
        transport.Responder = request =>
        {
            seen.Add(request.Method + " " + request.Path);
            return request.Path switch
            {
                "/v1/meetings" => DetailJson,
                "/v1/meetings/b1/process" => ProcessJson,
                _ => "{}",
            };
        };
        var audio = TempAudio();
        try
        {
            var client = new BackendClient(transport);
            var sync = new BackendSync(client);
            // R2: blank-only text is rejected by the server (400) — a count check is weaker than
            // the server's rule, so these must be skipped like an empty list.
            var result = await sync.SendAsync(MeetingWithAudio(audio),
                new List<Segment> { new("s1", "", 0, 10, "   ", true) }, new List<Speaker>(),
                new List<MediaTrack>(), existingBackendId: null);
            Assert.DoesNotContain(seen, s => s.Contains("/transcripts"));
            Assert.Contains("POST /v1/meetings/b1/process", seen);
            Assert.Equal(0, result.TranscriptVersion);
        }
        finally { File.Delete(audio); }
    }

    [Fact]
    public async Task Send_Resend_FetchesMediaDuration_BeforeTranscript()
    {
        var transport = new RecordingTransport();
        var seen = new List<string>();
        transport.Responder = request =>
        {
            seen.Add(request.Method + " " + request.Path);
            return request.Path switch
            {
                "/v1/meetings/b1/media" => "{\"items\":[{\"id\":\"a1\",\"meeting_id\":\"b1\",\"kind\":\"audio\",\"role\":\"primary\",\"original_name\":\"a.m4a\",\"content_type\":\"audio/mp4\",\"size_bytes\":1,\"sha256\":\"s\",\"duration_ms\":90000,\"start_offset_ms\":0,\"sample_rate\":null,\"channels\":null,\"created_at\":null}],\"total\":1,\"limit\":50,\"offset\":0}",
                "/v1/meetings/b1/transcripts" => TranscriptJson,
                "/v1/meetings/b1/process" => ProcessJson,
                _ => "{}",
            };
        };
        var client = new BackendClient(transport);
        var sync = new BackendSync(client);
        // Zero-duration meeting: R1 keeps the probe fetch on this path only, and end_ms follows it.
        await sync.SendAsync(MeetingNoDuration("C:/nonexistent/a.m4a"),
            new List<Segment> { new("s1", "", 20_500, 50_000, "hi", true) }, new List<Speaker>(),
            new List<MediaTrack>(), existingBackendId: "b1");
        Assert.Equal(new[] { "GET /v1/meetings/b1/media", "POST /v1/meetings/b1/transcripts", "POST /v1/meetings/b1/process" }, seen);
        Assert.Contains("\"start_ms\":20500,\"end_ms\":90000", transport.Requests[1].JsonBody);
    }

    [Fact]
    public async Task FirstSend_NullEndedAt_FallsBackToProbeDuration()
    {
        var transport = new RecordingTransport();
        transport.Responder = request => request.Path switch
        {
            "/v1/meetings" => DetailJson,
            "/v1/meetings/b1/media" => MediaUploadJson,
            "/v1/meetings/b1/transcripts" => TranscriptJson,
            "/v1/meetings/b1/process" => ProcessJson,
            _ => "{}",
        };
        var audio = TempAudio();
        try
        {
            var client = new BackendClient(transport);
            var sync = new BackendSync(client);
            // EndedAt = null -> alignMs = 0 -> the probe is the alignment target.
            await sync.SendAsync(MeetingNoDuration(audio),
                new List<Segment> { new("s1", "", 0, 10, "hi", true) }, new List<Speaker>(),
                new List<MediaTrack>(), existingBackendId: null);
            Assert.Contains("\"end_ms\":90000", transport.Requests[2].JsonBody);
        }
        finally { File.Delete(audio); }
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
