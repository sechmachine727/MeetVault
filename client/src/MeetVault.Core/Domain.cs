namespace MeetVault.Core;

public sealed record Speaker(string Id, string DisplayName)
{
    public Speaker Rename(string displayName) => this with { DisplayName = displayName };
}

public sealed record Segment(string Id, string SpeakerId, long StartMs, long EndMs, string Text, bool IsFinal)
{
    public Segment Edit(string text) => this with { Text = text };
    public Segment Reassign(string speakerId) => this with { SpeakerId = speakerId };
}

public enum MeetingStatus { Recording, Paused, Processing, Ready, Failed }

// Language: the meeting's active transcription language code ("en"/"ja"/"vi"), "" until
// the first write. Owned exclusively by SetMeetingLanguage — never touched by Save's
// upsert so checkpoints cannot clobber it.
public sealed record Meeting(string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, MeetingStatus Status, string? MediaPath, string? MediaMime, string Language = "");

// One screen-video run's file (kind "screen"). start_offset_ms is when the run began
// relative to the meeting start, so playback can align the MP4 against the m4a timeline.
public sealed record MediaTrack(string Id, string MeetingId, string Kind, string Path, string Mime, long StartOffsetMs, long DurationMs, string CreatedAt);

// DeletedIds: rows actually deleted (their managed recording was removed too, or there
// was none). FailedIds: rows kept, keyed by the reason. SkippedMedia: rows that WERE
// deleted but whose recording file survives — either because it is unmanaged (foreign
// path, relative, or behind a link below the media root) and still exists, or because
// the managed cleanup failed and the bytes remain at <media>.deleting — the UI must
// say so instead of promising the audio went away.
public sealed record BulkDeleteResult(
    IReadOnlyList<string> DeletedIds,
    IReadOnlyDictionary<string, string> FailedIds,
    IReadOnlyList<(string Id, string Path)> SkippedMedia);

// Local snapshot of the backend state for one meeting. State mirrors the server's meeting status
// ("uploaded"/"processing"/"ready"/"failed"/"deleted") plus local-only "sending" (in-flight) and
// "" (never sent). The server is the source of truth; this row drives offline display only.
public sealed record BackendSyncRow(string MeetingId, string? BackendId, string State, string? LastError, bool Mock, string UpdatedAt);

