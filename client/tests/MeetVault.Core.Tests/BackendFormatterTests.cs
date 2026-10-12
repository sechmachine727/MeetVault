using MeetVault.Core;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public sealed class BackendFormatterTests
{
    [Fact]
    public void ToFormatA_UsesRealTimings_AndSpeakerNames()
    {
        var meeting = new Meeting("mv-1", "Sync", DateTimeOffset.Parse("2026-10-12T02:30:00Z"), null, MeetingStatus.Ready, null, null, "en");
        var segments = new List<Segment>
        {
            new("s1", "spk_1", 0, 3200, "Welcome everyone.", true),
            new("s2", "spk_2", 3500, 6200, "The release slips a week.", true),
        };
        var speakers = new List<Speaker> { new("spk_1", "Speaker 1"), new("spk_2", "Speaker 2") };
        var json = BackendFormatter.ToFormatA(meeting, segments, speakers);
        Assert.Equal(
            "{\"source\":\"live_export\",\"language\":\"en\",\"segments\":[" +
            "{\"start_ms\":0,\"end_ms\":3200,\"text\":\"Welcome everyone.\",\"speaker_label\":\"Speaker 1\"}," +
            "{\"start_ms\":3500,\"end_ms\":6200,\"text\":\"The release slips a week.\",\"speaker_label\":\"Speaker 2\"}]}",
            json);
    }

    [Fact]
    public void ToFormatA_EmptyLanguage_IsOmitted_AndUnknownSpeaker_IsBlank()
    {
        var meeting = new Meeting("mv-2", "Solo", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null);
        var segments = new List<Segment> { new("s1", "", 10, 20, "hi", true) };
        var json = BackendFormatter.ToFormatA(meeting, segments, new List<Speaker>());
        Assert.DoesNotContain("language", json);
        Assert.Contains("\"speaker_label\":\"\"", json);
    }

    [Fact]
    public void ToFormatA_NoSegments_ProducesEmptyArray()
    {
        var meeting = new Meeting("mv-3", "Empty", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null, "ja");
        var json = BackendFormatter.ToFormatA(meeting, new List<Segment>(), new List<Speaker>());
        Assert.Equal("{\"source\":\"live_export\",\"language\":\"ja\",\"segments\":[]}", json);
    }

    [Fact]
    public void ToFormatA_ExtendsFinalEnd_ToMediaDuration()
    {
        var meeting = new Meeting("mv-4", "Extend", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null, "en");
        var segments = new List<Segment>
        {
            new("s1", "spk_1", 0, 20_000, "one", true),
            new("s2", "spk_1", 20_500, 50_000, "two", true),
        };
        var json = BackendFormatter.ToFormatA(meeting, segments, new List<Speaker>(), 90_000);
        Assert.Contains("\"start_ms\":0,\"end_ms\":20000", json); // earlier rows untouched
        Assert.Contains("\"start_ms\":20500,\"end_ms\":90000", json);
    }

    [Fact]
    public void ToFormatA_LeavesTimes_WhenMediaUnknown()
    {
        var meeting = new Meeting("mv-5", "NoProbe", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null, "en");
        var segments = new List<Segment> { new("s1", "", 0, 50_000, "one", true) };
        var json = BackendFormatter.ToFormatA(meeting, segments, new List<Speaker>(), null);
        Assert.Contains("\"start_ms\":0,\"end_ms\":50000", json);
    }

    [Fact]
    public void ToFormatA_LeavesTimes_WhenLastEndAtOrBeyondDuration()
    {
        var meeting = new Meeting("mv-6", "NoStretch", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null, "en");
        var segments = new List<Segment> { new("s1", "", 0, 50_000, "one", true) };
        Assert.Contains("\"start_ms\":0,\"end_ms\":50000", BackendFormatter.ToFormatA(meeting, segments, new List<Speaker>(), 50_000));
        Assert.Contains("\"start_ms\":0,\"end_ms\":50000", BackendFormatter.ToFormatA(meeting, segments, new List<Speaker>(), 30_000));
    }
}
