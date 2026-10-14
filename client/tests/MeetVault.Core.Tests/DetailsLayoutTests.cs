using MeetVault.Core;
using Xunit;

namespace MeetVault.Core.Tests;

public class DetailsLayoutTests
{
    [Theory]
    [InlineData(999, 1360, DetailsLayoutMode.Stacked)]
    [InlineData(1000, 1360, DetailsLayoutMode.TwoColumn)]
    [InlineData(1400, 519, DetailsLayoutMode.Stacked)]
    [InlineData(1400, 520, DetailsLayoutMode.TwoColumn)]
    [InlineData(999, 519, DetailsLayoutMode.Stacked)]
    [InlineData(1000, 520, DetailsLayoutMode.TwoColumn)]
    [InlineData(400, 300, DetailsLayoutMode.Stacked)]
    [InlineData(1679, 1360, DetailsLayoutMode.TwoColumn)]
    [InlineData(1680, 1360, DetailsLayoutMode.Wide)]
    [InlineData(1680, 519, DetailsLayoutMode.Stacked)]
    [InlineData(1680, 520, DetailsLayoutMode.Wide)]
    [InlineData(2000, 1360, DetailsLayoutMode.Wide)]
    [InlineData(2239, 1359, DetailsLayoutMode.Wide)]
    public void Decide_pins_the_boundaries(double width, double height, DetailsLayoutMode expected)
        => Assert.Equal(expected, DetailsLayout.Decide(width, height));

    [Fact]
    public void Thresholds_are_named_constants()
    {
        Assert.Equal(1000.0, DetailsLayout.MinTwoColumnWidthDip);
        Assert.Equal(520.0, DetailsLayout.MinTwoColumnHeightDip);
        Assert.Equal(1680.0, DetailsLayout.MinWideWidthDip);
    }
}
