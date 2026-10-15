using MeetVault.Core;
using Xunit;

namespace MeetVault.Core.Tests;

public class DetailsLayoutTests
{
    // The big-screen workspace (media left + analysis/transcript rail right) is kept at every
    // size its two regions fit in - the owner's direction: a smaller screen must show the same
    // UI as the big one. Measured content widths that must stay on the workspace: 1263 (the
    // 1600x1000 display, window maximized) and 1341 (a 1678x754 window).
    [Theory]
    [InlineData(1099, 1360, DetailsLayoutMode.Stacked)]
    [InlineData(1100, 1360, DetailsLayoutMode.Wide)]
    [InlineData(1100, 519, DetailsLayoutMode.Stacked)]
    [InlineData(1100, 520, DetailsLayoutMode.Wide)]
    [InlineData(999, 519, DetailsLayoutMode.Stacked)]
    [InlineData(400, 300, DetailsLayoutMode.Stacked)]
    [InlineData(1263, 712, DetailsLayoutMode.Wide)]
    [InlineData(1341, 721, DetailsLayoutMode.Wide)]
    [InlineData(2239, 1359, DetailsLayoutMode.Wide)]
    public void Decide_keeps_the_big_screen_workspace_whenever_it_fits(
        double width, double height, DetailsLayoutMode expected)
        => Assert.Equal(expected, DetailsLayout.Decide(width, height));

    [Fact]
    public void Thresholds_are_named_constants()
    {
        Assert.Equal(560.0, DetailsLayout.MinMediaWidthDip);
        Assert.Equal(480.0, DetailsLayout.MinRailWidthDip);
        Assert.Equal(12.0, DetailsLayout.PageSpacingDip);
        Assert.Equal(24.0, DetailsLayout.PagePaddingDip);
        Assert.Equal(520.0, DetailsLayout.MinWideHeightDip);
        Assert.Equal(1100.0, DetailsLayout.MinWideWidthDip);
    }

    // The width gate must never let the workspace be chosen where its own geometry does not fit
    // (that is what made the old 1000-DIP two-column tier crush the transport bar): the gate is
    // the two regions + the page's spacing and padding, and it is DERIVED from them, not typed in.
    [Fact]
    public void The_width_gate_fits_both_regions()
    {
        var needed = DetailsLayout.MinMediaWidthDip + DetailsLayout.MinRailWidthDip
                     + DetailsLayout.PageSpacingDip + 2 * DetailsLayout.PagePaddingDip;
        Assert.Equal(DetailsLayout.MinWideWidthDip, needed);
        Assert.Equal(DetailsLayoutMode.Wide, DetailsLayout.Decide(needed, 1000));
        Assert.Equal(DetailsLayoutMode.Stacked, DetailsLayout.Decide(needed - 1, 1000));
    }
}
