using System.Runtime.CompilerServices;
using System.Text.Json;
using MeetVault.Core;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

// Records every request and answers from a scripted responder — the seam the whole client rides on.
internal sealed class RecordingTransport : IBackendTransport
{
    public List<BackendRequest> Requests { get; } = new();
    public Func<BackendRequest, string> Responder { get; set; } = _ => "{}";
    public List<(string Path, string Destination)> Downloads { get; } = new();
    public List<string> StreamedPaths { get; } = new();
    public List<string> StreamLines { get; } = new();

    public Task<string> SendAsync(BackendRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Responder(request));
    }

    public Task DownloadToFileAsync(string path, string destinationPath, CancellationToken ct)
    {
        Downloads.Add((path, destinationPath));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        StreamedPaths.Add(path);
        foreach (var line in StreamLines) { yield return line; await Task.Yield(); }
    }

    public void Dispose() { }
}

public sealed class BackendClientTests
{
    private static readonly string DetailJson =
        """{"id":"b1","title":"Sync","status":"uploaded","language":"en","source":"meetvault","external_id":"mv-1","started_at":null,"ended_at":null,"duration_ms":1000,"deleted_at":null,"created_at":null,"updated_at":null,"media_count":0,"transcript_count":0,"active_transcript_id":null,"speaker_count":0,"has_analysis":false,"is_mock":false}""";

    [Fact]
    public async Task CreateMeeting_Posts_IdempotencyPair()
    {
        var transport = new RecordingTransport { Responder = _ => DetailJson };
        var client = new BackendClient(transport);
        var meeting = new Meeting("mv-1", "Sync", DateTimeOffset.Parse("2026-10-12T02:30:00Z"), null, MeetingStatus.Ready, null, null, "en");
        var created = await client.CreateMeetingAsync(meeting);
        Assert.Equal("b1", created.Id);
        var request = transport.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/meetings", request.Path);
        Assert.Contains("\"external_id\":\"mv-1\"", request.JsonBody);
        Assert.Contains("\"source\":\"meetvault\"", request.JsonBody);
        Assert.Contains("\"title\":\"Sync\"", request.JsonBody);
    }

    [Fact]
    public async Task UploadMedia_SendsKindRole_AndOffsetOnlyWhenPositive()
    {
        var transport = new RecordingTransport { Responder = _ => "{}" };
        var client = new BackendClient(transport);
        await client.UploadMediaAsync("b1", "C:/tmp/a.m4a", "audio/mp4", "audio", 0);
        await client.UploadMediaAsync("b1", "C:/tmp/s.mp4", "video/mp4", "video", 4200);
        Assert.Equal("/v1/meetings/b1/media", transport.Requests[0].Path);
        Assert.Equal("audio", transport.Requests[0].Query!["kind"]);
        Assert.Equal("primary", transport.Requests[0].Query!["role"]);
        Assert.False(transport.Requests[0].Query!.ContainsKey("start_offset_ms"));
        Assert.Equal("4200", transport.Requests[1].Query!["start_offset_ms"]);
        Assert.Equal("C:/tmp/s.mp4", transport.Requests[1].UploadPath);
        Assert.Equal("video/mp4", transport.Requests[1].UploadMime);
    }

    [Fact]
    public async Task Process_SendsStepsForceLanguage()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"meeting_id\":\"b1\",\"steps\":[\"analyze\"],\"job_ids\":[],\"enqueued\":0}" };
        var client = new BackendClient(transport);
        var result = await client.ProcessAsync("b1", new List<string> { "analyze" }, force: true, language: "en");
        Assert.Equal(0, result.Enqueued);
        Assert.Equal("{\"steps\":[\"analyze\"],\"language\":\"en\",\"force\":true}", transport.Requests[0].JsonBody);
    }

    [Fact]
    public async Task UploadTranscript_UsesAlignAuto_AndRawBody()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"id\":\"t1\",\"meeting_id\":\"b1\",\"version\":1,\"source\":\"live_export\",\"provider\":\"client\",\"model\":\"\",\"language\":\"en\",\"is_mock\":false,\"is_active\":true,\"segment_count\":1,\"created_at\":null,\"segments\":[],\"duration_ms\":5}" };
        var client = new BackendClient(transport);
        var formatA = "{\"source\":\"live_export\",\"language\":\"en\",\"segments\":[]}";
        var detail = await client.UploadTranscriptAsync("b1", formatA, "en");
        Assert.Equal(1, detail.Version);
        Assert.Equal("auto", transport.Requests[0].Query!["align"]);
        Assert.Equal(formatA, transport.Requests[0].JsonBody);
    }

    [Fact]
    public async Task Status_And_FindBackendId_And_ListMeetings()
    {
        var transport = new RecordingTransport();
        transport.Responder = request => request.Path switch
        {
            "/v1/meetings/b1/status" => "{\"status\":\"ready\",\"steps\":{},\"progress\":1.0,\"active_job_id\":null,\"active_transcript_id\":\"t1\",\"fresh_kinds\":[\"summary\"],\"stale_kinds\":[],\"mock\":true,\"duration_ms\":10}",
            "/v1/meetings" => "{\"items\":[{\"id\":\"b1\",\"title\":\"Sync\",\"status\":\"ready\",\"language\":\"en\",\"source\":\"meetvault\",\"external_id\":\"mv-1\",\"started_at\":null,\"ended_at\":null,\"duration_ms\":1,\"deleted_at\":null,\"created_at\":null,\"updated_at\":null}],\"total\":1,\"limit\":100,\"offset\":0}",
            _ => "{}",
        };
        var client = new BackendClient(transport);
        var status = await client.GetStatusAsync("b1");
        Assert.Equal("ready", status.Status);
        Assert.True(status.Mock);
        Assert.Equal("b1", await client.FindBackendIdAsync("mv-1"));
        Assert.Null(await client.FindBackendIdAsync("mv-other"));
        // The recovery lookup scans exactly the newest page (200) — pinned so a future edit cannot widen it silently.
        Assert.Equal("/v1/meetings", transport.Requests[1].Path);
        Assert.Equal("200", transport.Requests[1].Query!["limit"]);
    }

    [Fact]
    public async Task Analysis_FlattensBundle_And_ToleratesEmpty()
    {
        const string bundle = """
        {"meeting_id":"b1","transcript_id":"t1","run":{"id":"r1","transcript_id":"t1","provider":"mock-analyzer","model":"","prompt_version":"v1","is_mock":true,"status":"succeeded","created_at":null},
         "summary":{"text":"All good","bullets":["a","b"]},
         "agenda":{"items":[{"title":"Intro","topic":"welcome","start_ms":0,"end_ms":300000}]},
         "sections":[{"id":"s1","meeting_id":"b1","analysis_run_id":"r1","ordinal":1,"title":"Intro","topic":"welcome","start_ms":0,"end_ms":300000,"summary":"","is_mock":true}],
         "section_summaries":[{"id":"d1","section_id":"s1","kind":"section_summary","payload":{"summary":"hello","bullets":[],"decisions":[],"action_items":[]},"is_mock":true,"stale":false,"created_at":null}],
         "decisions":{"items":[{"text":"ship it","decided_by":["Speaker 1"],"at_ms":120000,"segment_ids":["seg-1","bogus"]}]},
         "action_items":{"items":[{"text":"do it","owner":"Speaker 2","due":null,"segment_ids":[]}]},
         "qa":[{"id":"q1","section_id":null,"kind":"qa","payload":{"question":"q","answer":"a","citations":[{"segment_id":"seg-1","start_ms":1,"end_ms":2,"text":"x"}],"dropped_citations":1},"is_mock":true,"stale":false,"created_at":null}],
         "stale_kinds":["summary"],"mock":true}
        """;
        var transport = new RecordingTransport { Responder = _ => bundle };
        var client = new BackendClient(transport);
        var parsed = await client.GetAnalysisAsync("b1");
        Assert.Equal("All good", parsed.Summary!.Text);
        Assert.Equal(2, parsed.Summary.Bullets.Count);
        Assert.Single(parsed.Agenda);
        Assert.Single(parsed.Sections);
        Assert.Equal("ship it", parsed.Decisions[0].Text);
        Assert.Equal(2, parsed.Decisions[0].SegmentIds.Count); // server guarantees resolution; we show what it stored
        Assert.Equal("Speaker 2", parsed.Actions[0].Owner);
        Assert.Null(parsed.Actions[0].Due);
        Assert.Equal(1, parsed.Qa[0].DroppedCitations);
        Assert.Equal("seg-1", parsed.Qa[0].Citations[0].SegmentId);
        Assert.True(parsed.Mock);

        transport.Responder = _ => "{\"meeting_id\":\"b1\",\"transcript_id\":null,\"run\":null,\"summary\":null,\"agenda\":null,\"sections\":[],\"section_summaries\":[],\"decisions\":null,\"action_items\":null,\"qa\":[],\"stale_kinds\":[],\"mock\":false}";
        var empty = await client.GetAnalysisAsync("b1");
        Assert.Null(empty.Summary);
        Assert.Empty(empty.Decisions);
        Assert.Empty(empty.Qa);
    }

    [Fact]
    public async Task Speakers_Rename_Merge_Delete_UseExactPathsAndBodies()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"merged_speaker_id\":\"s1\",\"target_speaker_id\":\"s2\",\"moved_segments\":3}" };
        var client = new BackendClient(transport);
        await client.RenameSpeakerAsync("s1", "Alice");
        await client.MergeSpeakersAsync("s1", "s2");
        await client.DeleteSpeakerAsync("s1");
        Assert.Equal("/v1/speakers/s1", transport.Requests[0].Path);
        Assert.Equal("PATCH", transport.Requests[0].Method);
        Assert.Equal("{\"label\":\"Alice\"}", transport.Requests[0].JsonBody);
        Assert.Equal("/v1/speakers/s1/merge", transport.Requests[1].Path);
        Assert.Equal("{\"target_speaker_id\":\"s2\"}", transport.Requests[1].JsonBody);
        Assert.Equal("DELETE", transport.Requests[2].Method);
    }

    [Fact]
    public async Task TranscriptVersions_List_Activate_Delete()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"items\":[{\"id\":\"t1\",\"meeting_id\":\"b1\",\"version\":1,\"source\":\"live_export\",\"provider\":\"client\",\"model\":\"\",\"language\":\"en\",\"is_mock\":false,\"is_active\":true,\"segment_count\":2,\"created_at\":null}],\"total\":1,\"limit\":50,\"offset\":0}" };
        var client = new BackendClient(transport);
        var versions = await client.ListTranscriptsAsync("b1");
        Assert.Single(versions);
        await client.ActivateTranscriptAsync("t2");
        await client.DeleteTranscriptAsync("t3");
        Assert.Equal("/v1/transcripts/t2/activate", transport.Requests[1].Path);
        Assert.Equal("DELETE", transport.Requests[2].Method);
    }

    [Fact]
    public async Task Qa_Ask_PostsQuestion()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"meeting_id\":\"b1\",\"job_id\":\"j9\",\"question\":\"q?\",\"state\":\"queued\"}" };
        var client = new BackendClient(transport);
        var job = await client.AskAsync("b1", "q?");
        Assert.Equal("j9", job.JobId);
        Assert.Equal("{\"question\":\"q?\",\"top_k\":8}", transport.Requests[0].JsonBody);
    }

    [Fact]
    public async Task Jobs_List_Get_Cancel()
    {
        var transport = new RecordingTransport();
        transport.Responder = request => request.Path switch
        {
            "/v1/meetings/b1/jobs" => "{\"items\":[{\"id\":\"j1\",\"meeting_id\":\"b1\",\"type\":\"transcribe\",\"state\":\"succeeded\",\"progress\":1.0,\"attempts\":1,\"max_attempts\":3,\"error\":null,\"is_mock\":true,\"run_after\":null,\"started_at\":null,\"finished_at\":null,\"created_at\":null}],\"total\":1,\"limit\":50,\"offset\":0}",
            "/v1/jobs/j1" => "{\"id\":\"j1\",\"meeting_id\":\"b1\",\"type\":\"transcribe\",\"state\":\"succeeded\",\"progress\":1.0,\"attempts\":1,\"max_attempts\":3,\"error\":null,\"is_mock\":true,\"events\":[{\"id\":1,\"at\":\"t\",\"level\":\"info\",\"message\":\"done\"}]}",
            _ => "{}",
        };
        var client = new BackendClient(transport);
        Assert.Single(await client.ListJobsAsync("b1"));
        Assert.Single((await client.GetJobAsync("j1")).Events);
        await client.CancelJobAsync("j1");
        Assert.Equal("/v1/jobs/j1/cancel", transport.Requests[2].Path);
    }

    [Fact]
    public async Task WatchJob_ParsesSse_AndSkipsGarbage()
    {
        var transport = new RecordingTransport();
        transport.StreamLines.AddRange(new[]
        {
            "event: ping", "", "data: {\"id\":1,\"at\":\"t1\",\"level\":\"info\",\"message\":\"first\"}",
            ": comment", "data: broken{", "data: {\"id\":2,\"at\":\"t2\",\"level\":\"info\",\"message\":\"second\"}",
        });
        var client = new BackendClient(transport);
        var events = new List<BackendJobEvent>();
        await foreach (var e in client.WatchJobAsync("j1")) events.Add(e);
        Assert.Equal(2, events.Count);
        Assert.Equal("first", events[0].Message);
        Assert.Equal("/v1/jobs/j1/events", transport.StreamedPaths[0]);
    }

    [Fact]
    public async Task DownloadMedia_UsesMediaPath()
    {
        var transport = new RecordingTransport();
        var client = new BackendClient(transport);
        await client.DownloadMediaAsync("a9", "C:/tmp/a9.m4a");
        Assert.Equal("/v1/media/a9", transport.Downloads[0].Path);
    }

    [Fact]
    public async Task ListMeetings_ClampsPageSizeAboveRange_DownTo200()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"items\":[],\"total\":0,\"limit\":200,\"offset\":0}" };
        var client = new BackendClient(transport);
        await client.ListMeetingsAsync(500);
        Assert.Equal("200", transport.Requests[0].Query!["limit"]);
    }

    [Fact]
    public async Task ListMeetings_ClampsPageSizeBelowRange_UpTo1()
    {
        var transport = new RecordingTransport { Responder = _ => "{\"items\":[],\"total\":0,\"limit\":1,\"offset\":0}" };
        var client = new BackendClient(transport);
        await client.ListMeetingsAsync(0);
        Assert.Equal("1", transport.Requests[0].Query!["limit"]);
    }

    [Fact]
    public async Task CreateMeeting_ClampsNegativeDuration_ToZero()
    {
        var transport = new RecordingTransport { Responder = _ => DetailJson };
        var client = new BackendClient(transport);
        var meeting = new Meeting("mv-neg", "Bad", DateTimeOffset.Parse("2026-10-12T02:30:00Z"),
            DateTimeOffset.Parse("2026-10-12T02:29:00Z"), MeetingStatus.Ready, null, null, "en");
        await client.CreateMeetingAsync(meeting);
        Assert.Contains("\"duration_ms\":0", transport.Requests[0].JsonBody);
    }
}
