using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class CaptionPlacementTests
{
    [Fact]
    public void BottomCenter_CentersHorizontallyAndSitsOnMargin()
    {
        // 1920x1040 work area, 1100x220 window, 24 px margin.
        var (x, y) = CaptionPlacement.BottomCenter(0, 0, 1920, 1040, 1100, 220, 24);
        Assert.Equal(410, x); // (1920 - 1100) / 2
        Assert.Equal(796, y); // 1040 - 220 - 24
    }

    [Fact]
    public void BottomCenter_ClampsInsideSmallWorkArea()
    {
        // Window wider AND taller than the area: must not go negative.
        var (x, y) = CaptionPlacement.BottomCenter(0, 0, 800, 100, 1100, 220, 24);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void FormatCaption_PrefixesSpeaker()
    {
        Assert.Equal("Alice: hello there", CaptionPlacement.FormatCaption("Alice", "hello there"));
    }

    [Fact]
    public void FormatCaption_TrimsSpeaker()
    {
        Assert.Equal("Alice: hi", CaptionPlacement.FormatCaption(" Alice ", "hi"));
    }

    [Fact]
    public void FormatCaption_EmptySpeaker_ReturnsTextOnly()
    {
        Assert.Equal("hello", CaptionPlacement.FormatCaption("", "hello"));
        Assert.Equal("hello", CaptionPlacement.FormatCaption("   ", "hello"));
    }
}
