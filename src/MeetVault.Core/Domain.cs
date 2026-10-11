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

public sealed record Meeting(string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, MeetingStatus Status, string? MediaPath, string? MediaMime);

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

public enum GeminiJobState { Queued, Uploading, Succeeded, Failed }

public sealed record GeminiJob(string Id, string MeetingId, GeminiJobState State, int Attempts, string? LastError, string? ResultPath);

