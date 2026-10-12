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
    }
}
