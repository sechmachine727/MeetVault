using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MeetVault.Core.Backend;

// Typed client over IBackendTransport: one method per endpoint the app uses. All HTTP error
// mapping happens in the transport (BackendApiException); this class never touches HttpClient.
public sealed class BackendClient
{
    private readonly IBackendTransport _transport;

    public BackendClient(IBackendTransport transport) => _transport = transport;

    public async Task<BackendReady> GetReadyAsync(CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/readyz"), ct), BackendJsonContext.Default.BackendReady);

    public async Task<Dictionary<string, BackendProviderInfo>> GetProvidersAsync(CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/system/providers"), ct), BackendJsonContext.Default.DictionaryStringBackendProviderInfo);

    public async Task<BackendSystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/system/info"), ct), BackendJsonContext.Default.BackendSystemInfo);

    public async Task<BackendMeetingDetail> CreateMeetingAsync(Meeting meeting, CancellationToken ct = default)
    {
        var body = new BackendMeetingCreateBody(
            meeting.Title,
            meeting.Id,
            "meetvault",
            string.IsNullOrEmpty(meeting.Language) ? null : meeting.Language,
            meeting.StartedAt.ToUniversalTime().ToString("o"),
            meeting.EndedAt?.ToUniversalTime().ToString("o"),
            meeting.EndedAt is { } end ? Math.Max(0, (long)(end - meeting.StartedAt).TotalMilliseconds) : 0);
        var json = JsonSerializer.Serialize(body, BackendJsonContext.Default.BackendMeetingCreateBody);
        return Parse(await _transport.SendAsync(new BackendRequest("POST", "/v1/meetings", JsonBody: json), ct),
            BackendJsonContext.Default.BackendMeetingDetail);
    }

    public async Task<BackendMeetingDetail> GetMeetingAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId), ct),
            BackendJsonContext.Default.BackendMeetingDetail);

    public async Task<List<BackendMeeting>> ListMeetingsAsync(int limit = 100, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["limit"] = Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture) };
        var page = Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings", query), ct),
            BackendJsonContext.Default.BackendPageBackendMeeting);
        return page.Items;
    }

    // Recovery path: the local backend_id was lost but the meeting exists server-side.
    public async Task<string?> FindBackendIdAsync(string externalId, CancellationToken ct = default)
    {
        foreach (var meeting in await ListMeetingsAsync(200, ct))
            if (string.Equals(meeting.ExternalId, externalId, StringComparison.Ordinal)) return meeting.Id;
        return null;
    }

    public async Task<BackendMediaUpload> UploadMediaAsync(string backendId, string filePath, string mime, string kind, long startOffsetMs, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["kind"] = kind, ["role"] = "primary" };
        if (startOffsetMs > 0) query["start_offset_ms"] = startOffsetMs.ToString(CultureInfo.InvariantCulture);
        var body = await _transport.SendAsync(new BackendRequest("POST", "/v1/meetings/" + backendId + "/media", query,
            UploadPath: filePath, UploadMime: mime), ct);
        return Parse(body, BackendJsonContext.Default.BackendMediaUpload);
    }

    public async Task<List<BackendMediaAsset>> ListMediaAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/media"), ct),
            BackendJsonContext.Default.BackendPageBackendMediaAsset).Items;

    public async Task<BackendTranscriptDetail> UploadTranscriptAsync(string backendId, string formatAJson, string language, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["align"] = "auto" };
        if (!string.IsNullOrEmpty(language)) query["language"] = language;
        return Parse(await _transport.SendAsync(new BackendRequest("POST", "/v1/meetings/" + backendId + "/transcripts", query, JsonBody: formatAJson), ct),
            BackendJsonContext.Default.BackendTranscriptDetail);
    }

    public async Task<BackendTranscriptDetail> GetActiveTranscriptAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/transcript"), ct),
            BackendJsonContext.Default.BackendTranscriptDetail);

    public Task<string> GetActiveTranscriptTextAsync(string backendId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/transcript/text"), ct);

    public async Task<List<BackendTranscript>> ListTranscriptsAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/transcripts"), ct),
            BackendJsonContext.Default.BackendPageBackendTranscript).Items;

    public Task ActivateTranscriptAsync(string transcriptId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("POST", "/v1/transcripts/" + transcriptId + "/activate"), ct);

    public Task DeleteTranscriptAsync(string transcriptId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("DELETE", "/v1/transcripts/" + transcriptId), ct);

    public async Task<BackendProcessResult> ProcessAsync(string backendId, IReadOnlyList<string> steps, bool force, string? language = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new BackendProcessBody(steps.ToList(), language, force),
            BackendJsonContext.Default.BackendProcessBody);
        return Parse(await _transport.SendAsync(new BackendRequest("POST", "/v1/meetings/" + backendId + "/process", JsonBody: json), ct),
            BackendJsonContext.Default.BackendProcessResult);
    }

    public async Task<BackendStatus> GetStatusAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/status"), ct),
            BackendJsonContext.Default.BackendStatus);

    public async Task<BackendBundle> GetAnalysisAsync(string backendId, CancellationToken ct = default)
        => BackendAnalysisParser.Flatten(Parse(
            await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/analysis"), ct),
            BackendJsonContext.Default.BackendBundleRaw));

    // Raw fetch for the on-disk cache (exact server payload, future-proof).
    public Task<string> GetTranscriptRawAsync(string backendId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/transcript"), ct);

    public Task<string> GetAnalysisRawAsync(string backendId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/analysis"), ct);

    public Task DeleteMeetingAsync(string backendId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("DELETE", "/v1/meetings/" + backendId), ct);

    public Task DownloadMediaAsync(string assetId, string destinationPath, CancellationToken ct = default)
        => _transport.DownloadToFileAsync("/v1/media/" + assetId, destinationPath, ct);

    public async Task<BackendQaJob> AskAsync(string backendId, string question, string? sectionId = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new BackendQaBody(question, sectionId, 8), BackendJsonContext.Default.BackendQaBody);
        return Parse(await _transport.SendAsync(new BackendRequest("POST", "/v1/meetings/" + backendId + "/qa", JsonBody: json), ct),
            BackendJsonContext.Default.BackendQaJob);
    }

    public async Task<List<BackendSpeaker>> ListSpeakersAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/speakers"), ct),
            BackendJsonContext.Default.ListBackendSpeaker);

    public async Task RenameSpeakerAsync(string speakerId, string label, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new BackendSpeakerPatchBody(label), BackendJsonContext.Default.BackendSpeakerPatchBody);
        await _transport.SendAsync(new BackendRequest("PATCH", "/v1/speakers/" + speakerId, JsonBody: json), ct);
    }

    public async Task<BackendSpeakerMerge> MergeSpeakersAsync(string speakerId, string targetSpeakerId, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new BackendSpeakerMergeBody(targetSpeakerId), BackendJsonContext.Default.BackendSpeakerMergeBody);
        return Parse(await _transport.SendAsync(new BackendRequest("POST", "/v1/speakers/" + speakerId + "/merge", JsonBody: json), ct),
            BackendJsonContext.Default.BackendSpeakerMerge);
    }

    public Task DeleteSpeakerAsync(string speakerId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("DELETE", "/v1/speakers/" + speakerId), ct);

    public async Task<List<BackendJob>> ListJobsAsync(string backendId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/meetings/" + backendId + "/jobs"), ct),
            BackendJsonContext.Default.BackendPageBackendJob).Items;

    public async Task<BackendJobDetail> GetJobAsync(string jobId, CancellationToken ct = default)
        => Parse(await _transport.SendAsync(new BackendRequest("GET", "/v1/jobs/" + jobId), ct),
            BackendJsonContext.Default.BackendJobDetail);

    public Task CancelJobAsync(string jobId, CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("POST", "/v1/jobs/" + jobId + "/cancel"), ct);

    // SSE lines arrive as "data: {json}"; the server closes the stream on a terminal job state.
    // Lenient prefix: accept "data:" with or without the space. Multi-line data: frames are
    // unsupported — the server emits single-line frames only.
    public async IAsyncEnumerable<BackendJobEvent> WatchJobAsync(string jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in _transport.StreamLinesAsync("/v1/jobs/" + jobId + "/events", ct))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].TrimStart();
            if (payload.Length == 0) continue;
            BackendJobEvent? jobEvent;
            try { jobEvent = JsonSerializer.Deserialize(payload, BackendJsonContext.Default.BackendJobEvent); }
            catch (JsonException) { continue; }
            if (jobEvent is not null) yield return jobEvent;
        }
    }

    private static T Parse<T>(string body, JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(body, typeInfo)!;
}
