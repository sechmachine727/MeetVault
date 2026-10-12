using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public sealed class BackendPresentationTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(0.999, 100)]
    [InlineData(1.4, 100)]
    [InlineData(-0.2, 0)]
    public void Percent_Clamps(double progress, int expected) => Assert.Equal(expected, BackendPresentation.Percent(progress));

    [Theory]
    [InlineData("uploaded", "Back.State.Uploaded")]
    [InlineData("processing", "Back.State.Processing")]
    [InlineData("ready", "Back.State.Ready")]
    [InlineData("failed", "Back.State.Failed")]
    [InlineData("deleted", "Back.State.Deleted")]
    [InlineData("sending", "Back.State.Sending")]
    [InlineData("", "Back.State.None")]
    [InlineData("weird", "Back.State.Unknown")]
    public void StatusKey_Maps_ServerStates(string status, string expected)
        => Assert.Equal(expected, BackendPresentation.StatusKey(status));

    [Fact]
    public void StepsSummary_Orders_Pipeline_ThenExtras()
    {
        var steps = new Dictionary<string, string>
        {
            ["analyze"] = "queued", ["transcribe"] = "running", ["zzz"] = "x",
        };
        Assert.Equal("transcribe: running · analyze: queued · zzz: x", BackendPresentation.StepsSummary(steps));
        Assert.Equal("", BackendPresentation.StepsSummary(new Dictionary<string, string>()));
    }

    [Fact]
    public void FormatSpeakerLine_IncludesTimeAndLabel()
    {
        Assert.Equal("[00:03] Speaker 1", BackendPresentation.SpeakerLine(3200, "Speaker 1"));
        Assert.Equal("[01:02:03] ", BackendPresentation.SpeakerLine(3723000, ""));
    }
}
