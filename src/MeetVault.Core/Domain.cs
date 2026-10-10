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

public enum GeminiJobState { Queued, Uploading, Succeeded, Failed }

public sealed record GeminiJob(string Id, string MeetingId, GeminiJobState State, int Attempts, string? LastError, string? ResultPath);

