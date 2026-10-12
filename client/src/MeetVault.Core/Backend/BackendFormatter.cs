using System.Text.Json;

namespace MeetVault.Core.Backend;

public static class BackendFormatter
{
    // Format A: the client's authoritative transcript with REAL timings. The backend's align=auto
    // scales a fully-timed payload by duration_ms / max(end_ms) (backend/src/mvb/pipeline/align.py),
    // which would stretch every timestamp when the transcript ends before the recording. Extending
    // the final segment to the probed media duration keeps that factor at exactly 1 (identity).
    public static string ToFormatA(Meeting meeting, IReadOnlyList<Segment> segments, IReadOnlyList<Speaker> speakers,
        long? mediaDurationMs = null)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var speaker in speakers) names[speaker.Id] = speaker.DisplayName;
        var rows = new List<BackendFormatASegment>(segments.Count);
        foreach (var segment in segments)
        {
            rows.Add(new BackendFormatASegment(
                segment.StartMs, segment.EndMs, segment.Text,
                names.TryGetValue(segment.SpeakerId, out var name) ? name : ""));
        }
        if (mediaDurationMs is { } duration && rows.Count > 0 && rows[^1].EndMs < duration)
            rows[^1] = rows[^1] with { EndMs = duration };
        var payload = new BackendFormatATranscript(
            "live_export",
            string.IsNullOrEmpty(meeting.Language) ? null : meeting.Language,
            rows);
        return JsonSerializer.Serialize(payload, BackendJsonContext.Default.BackendFormatATranscript);
    }
}
