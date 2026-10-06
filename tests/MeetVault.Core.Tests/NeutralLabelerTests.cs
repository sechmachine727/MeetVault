using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class NeutralLabelerTests
{
    [Fact]
    public void Neutral_StablePerSpeaker()
    {
        var labeler = new NeutralSpeakerLabeler();
        var a1 = labeler.Label("spk_1", Gender.Male);
        var b1 = labeler.Label("spk_2", Gender.Female);
        Assert.Equal(a1, labeler.Label("spk_1", Gender.Female));
        Assert.NotEqual(a1, b1);
        Assert.StartsWith("Speaker ", a1);
    }
}
