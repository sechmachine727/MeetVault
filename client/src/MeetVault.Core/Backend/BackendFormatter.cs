using System.Text.Json;

namespace MeetVault.Core.Backend;

public static class BackendFormatter
{
    // Format A: the client's authoritative transcript with REAL timings. The backend's default
    // align=auto keeps these times and only fills untimed gaps; nothing is rescaled.
    public static string ToFormatA(Meeting meeting, IReadOnlyList<Segment> segments, IReadOnlyList<Speaker> speakers)
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
        var payload = new BackendFormatATranscript(
            "live_export",
            string.IsNullOrEmpty(meeting.Language) ? null : meeting.Language,
            rows);
        return JsonSerializer.Serialize(payload, BackendJsonContext.Default.BackendFormatATranscript);
    }
}
