using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class SpeakerLabelerTests
{
    [Fact]
    public void Assign_MixedTurns_UsesSeparateCounters()
    {
        var labeler = new SpeakerLabeler();
        Assert.Equal("Male 1", labeler.Assign(Gender.Male));
        Assert.Equal("Female 1", labeler.Assign(Gender.Female));
        Assert.Equal("Male 2", labeler.Assign(Gender.Male));
    }

    [Fact]
    public void Assign_Unknown_DoesNotConsumeCounter()
    {
        var labeler = new SpeakerLabeler();
        Assert.Equal("Unknown", labeler.Assign(Gender.Unknown));
        Assert.Equal("Male 1", labeler.Assign(Gender.Male));
    }
}
