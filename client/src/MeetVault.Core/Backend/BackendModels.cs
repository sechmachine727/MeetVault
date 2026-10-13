using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetVault.Core.Backend;

// Wire models for the Meetvault_Backend REST API (see backend/docs/API.md). JSON is snake_case
// via the source-gen context below; the few ambiguous names (sha256) are pinned explicitly.
// Datetime fields stay strings (display-only; the server owns time semantics).

public sealed record BackendProviderInfo(string Requested, string Effective, bool IsMock, string Model, string Reason);
public sealed record BackendReady(string Status, Dictionary<string, bool> Checks, Dictionary<string, BackendProviderInfo> Providers);
public sealed record BackendSystemInfo(string Version, string Env, string MigrationHead, string MediaRoot, long MaxUploadMb);
public sealed record BackendPage<T>(List<T> Items, int Total, int Limit, int Offset);

public sealed record BackendMeeting(
    string Id, string Title, string Status, string Language, string Source, string? ExternalId,
    string? StartedAt, string? EndedAt, long DurationMs, string? DeletedAt, string? CreatedAt, string? UpdatedAt);

public sealed record BackendMeetingDetail(
    string Id, string Title, string Status, string Language, string Source, string? ExternalId,
    string? StartedAt, string? EndedAt, long DurationMs, string? DeletedAt, string? CreatedAt, string? UpdatedAt,
    int MediaCount, int TranscriptCount, string? ActiveTranscriptId, int SpeakerCount, bool HasAnalysis, bool IsMock);

public sealed record BackendMediaAsset(
    string Id, string MeetingId, string Kind, string Role, string OriginalName, string ContentType,
    long SizeBytes, [property: JsonPropertyName("sha256")] string Sha256,
    long DurationMs, long StartOffsetMs, int? SampleRate, int? Channels, string? CreatedAt);

public sealed record BackendMediaUpload(
    string AssetId, long SizeBytes, [property: JsonPropertyName("sha256")] string Sha256,
    long DurationMs, int? SampleRate, int? Channels, bool IsVideo, string? ProbeError);

public sealed record BackendTranscript(
    string Id, string MeetingId, int Version, string Source, string Provider, string Model,
    string Language, bool IsMock, bool IsActive, int SegmentCount, string? CreatedAt);

public sealed record BackendSegment(
    string Id, string? SpeakerId, string SpeakerLabel, long StartMs, long EndMs, string Text,
    double? Confidence, string Source);

public sealed record BackendTranscriptDetail(
    string Id, string MeetingId, int Version, string Source, string Provider, string Model,
    string Language, bool IsMock, bool IsActive, int SegmentCount, string? CreatedAt,
    List<BackendSegment> Segments, long DurationMs);

public sealed record BackendSpeaker(
    string Id, string MeetingId, string Label, string Kind, string Color, string? CreatedAt,
    int SegmentCount, long SpeakingMs, string Preview);

public sealed record BackendJob(
    string Id, string? MeetingId, string Type, string State, double Progress, int Attempts,
    int MaxAttempts, string? Error, bool IsMock, string? RunAfter, string? StartedAt, string? FinishedAt, string? CreatedAt);

public sealed record BackendJobEvent(int Id, string At, string Level, string Message);
public sealed record BackendJobDetail(
    string Id, string? MeetingId, string Type, string State, double Progress, int Attempts,
    int MaxAttempts, string? Error, bool IsMock, List<BackendJobEvent> Events);

public sealed record BackendProcessResult(string MeetingId, List<string> Steps, List<string> JobIds, int Enqueued);
public sealed record BackendStatus(
    string Status, Dictionary<string, string> Steps, double Progress, string? ActiveJobId,
    string? ActiveTranscriptId, List<string> FreshKinds, List<string> StaleKinds, bool Mock, long DurationMs);
public sealed record BackendSection(
    string Id, string MeetingId, string AnalysisRunId, int Ordinal, string Title, string Topic,
    long StartMs, long EndMs, string Summary, bool IsMock);
public sealed record BackendAnalysisRun(
    string Id, string TranscriptId, string Provider, string Model, string PromptVersion,
    bool IsMock, string Status, string? CreatedAt);
public sealed record BackendQaJob(string MeetingId, string JobId, string Question, string State);
public sealed record BackendSpeakerMerge(string MergedSpeakerId, string TargetSpeakerId, int MovedSegments);

// Raw analysis bundle (dict payloads kept as JsonElement); BackendAnalysisParser flattens it.
public sealed record BackendDocumentRaw(string Id, string? SectionId, string Kind, JsonElement Payload, bool IsMock, bool Stale, string? CreatedAt);
public sealed record BackendBundleRaw(
    string MeetingId, string? TranscriptId, BackendAnalysisRun? Run,
    JsonElement? Summary, JsonElement? Agenda, List<BackendSection>? Sections,
    List<BackendDocumentRaw>? SectionSummaries, JsonElement? Decisions, JsonElement? ActionItems,
    List<BackendDocumentRaw>? Qa, List<string>? StaleKinds, bool Mock);

// Flattened view the UI consumes.
public sealed record BackendBundle(
    string MeetingId, string? TranscriptId, BackendAnalysisRun? Run,
    BackendSummaryView? Summary, List<BackendAgendaItem> Agenda, List<BackendSection> Sections,
    List<BackendSectionSummary> SectionSummaries, List<BackendDecision> Decisions,
    List<BackendActionItem> Actions, List<BackendQaItem> Qa, List<string> StaleKinds, bool Mock);
public sealed record BackendSummaryView(string Text, List<string> Bullets);
public sealed record BackendAgendaItem(string Title, string Topic, long StartMs, long EndMs);
public sealed record BackendSectionSummary(string SectionId, string Summary, List<string> Bullets, List<string> Decisions, List<string> ActionItems, bool IsMock);
public sealed record BackendDecision(string Text, List<string> DecidedBy, long? AtMs, List<string> SegmentIds);
public sealed record BackendActionItem(string Text, string Owner, string? Due, List<string> SegmentIds);
public sealed record BackendQaItem(string Id, string Question, string Answer, List<BackendCitation> Citations, int DroppedCitations, bool IsMock, bool Stale, string? CreatedAt);
public sealed record BackendCitation(string SegmentId, long StartMs, long EndMs, string Text);

// Outbound bodies.
public sealed record BackendMeetingCreateBody(string Title, string? ExternalId, string Source, string? Language, string? StartedAt, string? EndedAt, long DurationMs);
public sealed record BackendProcessBody(List<string> Steps, string? Language, bool Force);
public sealed record BackendQaBody(string Question, string? SectionId, int TopK);
public sealed record BackendSpeakerPatchBody(string Label);
public sealed record BackendSpeakerMergeBody(string TargetSpeakerId);
public sealed record BackendFormatATranscript(string Source, string? Language, List<BackendFormatASegment> Segments);
public sealed record BackendFormatASegment(long StartMs, long EndMs, string Text, string SpeakerLabel);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LoginBody))]
[JsonSerializable(typeof(BackendSessionInfo))]
[JsonSerializable(typeof(BackendMeInfo))]
[JsonSerializable(typeof(BackendUser))]
[JsonSerializable(typeof(BackendMeetingCreateBody))]
[JsonSerializable(typeof(BackendProcessBody))]
[JsonSerializable(typeof(BackendQaBody))]
[JsonSerializable(typeof(BackendSpeakerPatchBody))]
[JsonSerializable(typeof(BackendSpeakerMergeBody))]
[JsonSerializable(typeof(BackendFormatATranscript))]
[JsonSerializable(typeof(BackendReady))]
[JsonSerializable(typeof(Dictionary<string, BackendProviderInfo>))]
[JsonSerializable(typeof(BackendSystemInfo))]
[JsonSerializable(typeof(BackendPage<BackendMeeting>))]
[JsonSerializable(typeof(BackendMeetingDetail))]
[JsonSerializable(typeof(BackendMediaUpload))]
[JsonSerializable(typeof(BackendPage<BackendMediaAsset>))]
[JsonSerializable(typeof(BackendTranscriptDetail))]
[JsonSerializable(typeof(BackendPage<BackendTranscript>))]
[JsonSerializable(typeof(List<BackendSpeaker>))]
[JsonSerializable(typeof(BackendProcessResult))]
[JsonSerializable(typeof(BackendStatus))]
[JsonSerializable(typeof(BackendBundleRaw))]
[JsonSerializable(typeof(BackendQaJob))]
[JsonSerializable(typeof(BackendSpeakerMerge))]
[JsonSerializable(typeof(BackendJobDetail))]
[JsonSerializable(typeof(BackendPage<BackendJob>))]
[JsonSerializable(typeof(BackendJobEvent))]
public sealed partial class BackendJsonContext : JsonSerializerContext
{
}
