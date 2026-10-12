using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The overlay strip's conversation must never outgrow its cell (WinUI paints a
// Grid child unclipped, so an over-tall bottom-anchored panel covers the header
// and its top lines leave the window). These pin the pure fitting math against
// the strip's real budget: the measured cell is 169 DIP and the panel carries a
// 12 DIP bottom margin, leaving 157 DIP of text; line spacing is 6 DIP and every
// line renders at the same size (owner convention 2026-10-11).
public sealed class OverlayLayoutTests
{
    private const double Budget = 157; // measured: 169 DIP cell − 12 DIP panel margin
    private const double Spacing = 6;

    [Fact]
    public void FittingLines_TwelvePt_SevenLinesFit()
    {
        Assert.Equal(7, OverlayLayout.FittingLines(Budget, 12, Spacing));
    }

    [Fact]
    public void FittingLines_SixteenPt_FiveLinesFit()
    {
        Assert.Equal(5, OverlayLayout.FittingLines(Budget, 16, Spacing));
    }

    [Fact]
    public void FittingLines_FortyEightPt_TwoLinesFit()
    {
        Assert.Equal(2, OverlayLayout.FittingLines(Budget, 48, Spacing));
    }

    [Fact]
    public void FittingLines_SixteenPt_UnboundedTakeWouldOverflowTheStrip()
    {
        // The pre-fix code took min(ViewLines, 12) = 10 lines regardless of the
        // cell: 10 lines at 16 pt need 270 DIP against the 157 DIP budget — ~113 DIP
        // painted over the header and above the window. The fit allows 5, and the
        // fitted height stays inside the budget.
        var fitting = OverlayLayout.FittingLines(Budget, 16, Spacing);
        Assert.Equal(5, fitting);
        Assert.True(TenLineHeight() > Budget);
        Assert.True(FittedHeight(fitting) <= Budget);
        Assert.True(fitting < 10);

        static double TenLineHeight()
            => 10 * (16 * OverlayLayout.LineHeightFactor) + 9 * Spacing;
        static double FittedHeight(int n)
            => n * (16 * OverlayLayout.LineHeightFactor) + (n - 1) * Spacing;
    }

    [Fact]
    public void FittingLines_FortyEightPt_UnboundedTakeWouldOverflowTheStrip()
    {
        // 10 lines at 48 pt need 702 DIP; the fit allows 2.
        var fitting = OverlayLayout.FittingLines(Budget, 48, Spacing);
        Assert.Equal(2, fitting);
        Assert.True(TenLineHeight() > Budget);
        Assert.True(fitting < 10);

        static double TenLineHeight()
            => 10 * (48 * OverlayLayout.LineHeightFactor) + 9 * Spacing;
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(48)]
    public void FittingLines_FittedCountIsMaximalForTheStripBudget(double font)
    {
        var n = OverlayLayout.FittingLines(Budget, font, Spacing);
        Assert.True(Height(n, font) <= Budget, $"fitting={n} exceeds the budget");
        Assert.True(Height(n + 1, font) > Budget, $"fitting+1={n + 1} still fits — undercount");
        Assert.True(n >= 1, "at least one line is always shown");

        static double Height(int n, double font)
            => n * (font * OverlayLayout.LineHeightFactor) + (n - 1) * Spacing;
    }

    [Fact]
    public void FittingLines_NoSpacing_TighterPacking()
    {
        Assert.Equal(7, OverlayLayout.FittingLines(Budget, 16, 0));
        // A negative spacing is treated as none — never as extra room.
        Assert.Equal(7, OverlayLayout.FittingLines(Budget, 16, -6));
    }

    [Fact]
    public void FittingLines_ExactBoundary_OneLineFitsAtItsLineBox()
    {
        Assert.Equal(1, OverlayLayout.FittingLines(21.6, 16, 0)); // exactly one 16 pt line box
        Assert.Equal(1, OverlayLayout.FittingLines(21.6, 16, Spacing));
        // Below one line box a line is still the mandatory floor (≥1 whenever
        // available > 0) — the caller clips the overflow.
        Assert.Equal(1, OverlayLayout.FittingLines(21.59, 16, 0));
        Assert.Equal(1, OverlayLayout.FittingLines(21.59, 16, Spacing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    public void FittingLines_DegenerateAvailable_ReturnsZero(double available)
    {
        Assert.Equal(0, OverlayLayout.FittingLines(available, 16, Spacing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-16)]
    [InlineData(double.NaN)]
    public void FittingLines_DegenerateFont_ReturnsZero(double font)
    {
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, font, Spacing));
    }

    [Fact]
    public void FittingLines_DegenerateSpacing_ReturnsZero()
    {
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, 16, double.NaN));
    }

    [Fact]
    public void FittingLines_AbsurdlyLargeAvailable_Saturates()
    {
        Assert.Equal(int.MaxValue, OverlayLayout.FittingLines(double.MaxValue, 16, Spacing));
    }
}
