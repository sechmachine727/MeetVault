using MeetVault.Core;

namespace MeetVault.Core.Tests;

// No local gender classifier in the MVP: labels must fall back to distinct neutral
// names, never two speakers both reading "Unknown".
public sealed class CounterGenderFallbackTests
{
    [Fact]
    public void NoEstimator_ProducesDistinctNeutralNames()
    {
        var labeler = new CounterGenderLabeler();
        var a = labeler.Label("spk_1", Gender.Unknown);
        var b = labeler.Label("spk_2", Gender.Unknown);
        Assert.NotEqual(a, b);
        Assert.StartsWith("Speaker ", a);
        Assert.StartsWith("Speaker ", b);
    }

    [Fact]
    public void WithEstimator_UsesGenderedCounters()
    {
        var labeler = new CounterGenderLabeler(id => id == "spk_m" ? Gender.Male : Gender.Female);
        Assert.Equal("Male 1", labeler.Label("spk_m", Gender.Unknown));
        Assert.Equal("Female 1", labeler.Label("spk_f", Gender.Unknown));
    }
}

