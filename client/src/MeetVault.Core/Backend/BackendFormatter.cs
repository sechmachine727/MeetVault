using System.Text.Json;

namespace MeetVault.Core.Backend;

public static class BackendFormatter
{
    // Format A: the client's authoritative transcript with REAL timings. The backend's align=auto
    // scales a fully-timed payload by duration_ms / max(end_ms) (backend/src/mvb/pipeline/align.py),
    // which would stretch every timestamp when the transcript ends before the recording. The caller
    // passes the server's alignment target — meeting.duration_ms (EndedAt-StartedAt) when nonzero,
    // else the audio probe — and extending the max-end row to it keeps that factor at exactly 1
    // (identity). When the target is SMALLER than the transcript span, times are left alone and the
    // server's downscale applies — accepted server behavior.
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
        if (mediaDurationMs is { } duration && rows.Count > 0)
        {
            // align.py divides by max(end_ms) over ALL rows: extend the max-end row (not merely the
            // last one) so unsorted input still gets the identity factor (review 2).
            var maxIndex = 0;
            for (var i = 1; i < rows.Count; i++) if (rows[i].EndMs > rows[maxIndex].EndMs) maxIndex = i;
            if (rows[maxIndex].EndMs < duration) rows[maxIndex] = rows[maxIndex] with { EndMs = duration };
        }
        var payload = new BackendFormatATranscript(
            "live_export",
            string.IsNullOrEmpty(meeting.Language) ? null : meeting.Language,
            rows);
        return JsonSerializer.Serialize(payload, BackendJsonContext.Default.BackendFormatATranscript);
    }
}
