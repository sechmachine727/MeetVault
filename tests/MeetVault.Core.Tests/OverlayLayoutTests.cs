using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The overlay strip's conversation must never outgrow its cell (WinUI paints a
// Grid child unclipped, so an over-tall bottom-anchored panel covers the header
// and its top lines leave the window). These pin the pure fitting math against
// the strip's real budget: the measured cell is 169 DIP and the panel carries a
// 12 DIP bottom margin, leaving 157 DIP of text; line spacing is 6 DIP and the
// newest line renders 12 DIP larger than the rest.
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
    public void FittingLines_NewestLarger_TwelvePt_SixLinesFit()
    {
        // 24 pt newest: 5 older lines at 12 + the 24 pt line = 143.4 ≤ 157;
        // a 7th line (165.6) does not fit.
        Assert.Equal(6, OverlayLayout.FittingLines(Budget, 12, 24, Spacing));
    }

    [Fact]
    public void FittingLines_NewestLarger_SixteenPt_FiveLinesFit()
    {
        Assert.Equal(5, OverlayLayout.FittingLines(Budget, 16, 28, Spacing));
    }

    [Fact]
    public void FittingLines_NewestLarger_FortyEightPt_TwoLinesFit()
    {
        Assert.Equal(2, OverlayLayout.FittingLines(Budget, 48, 60, Spacing));
    }

    [Fact]
    public void FittingLines_SixteenPt_UnboundedTakeWouldOverflowTheStrip()
    {
        // The pre-fix code took min(ViewLines, 12) = 10 lines regardless of the
        // cell: 10 lines at 16 pt with a 28 pt newest need 286.2 DIP against the
        // 157 DIP budget — ~129 DIP painted over the header and above the window.
        // The fit allows 5, and the fitted height stays inside the budget.
        var fitting = OverlayLayout.FittingLines(Budget, 16, 28, Spacing);
        Assert.Equal(5, fitting);
        Assert.True(TenLineHeight() > Budget);
        Assert.True(FittedHeight(fitting) <= Budget);
        Assert.True(fitting < 10);

        static double TenLineHeight()
            => 9 * (16 * OverlayLayout.LineHeightFactor + Spacing) + 28 * OverlayLayout.LineHeightFactor;
        static double FittedHeight(int n)
            => (n - 1) * (16 * OverlayLayout.LineHeightFactor + Spacing) + 28 * OverlayLayout.LineHeightFactor;
    }

    [Fact]
    public void FittingLines_FortyEightPt_UnboundedTakeWouldOverflowTheStrip()
    {
        // 10 lines at 48 pt with a 60 pt newest need 718.2 DIP; the fit allows 2.
        var fitting = OverlayLayout.FittingLines(Budget, 48, 60, Spacing);
        Assert.Equal(2, fitting);
        Assert.True(TenLineHeight() > Budget);
        Assert.True(fitting < 10);

        static double TenLineHeight()
            => 9 * (48 * OverlayLayout.LineHeightFactor + Spacing) + 60 * OverlayLayout.LineHeightFactor;
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(48)]
    public void FittingLines_FittedCountIsMaximalForTheStripBudget(double font)
    {
        var n = OverlayLayout.FittingLines(Budget, font, font + 12, Spacing);
        Assert.True(Height(n, font) <= Budget, $"fitting={n} exceeds the budget");
        Assert.True(Height(n + 1, font) > Budget, $"fitting+1={n + 1} still fits — undercount");
        Assert.True(n >= 1, "the newest line is always shown");

        static double Height(int n, double font)
            => (n - 1) * (font * OverlayLayout.LineHeightFactor + Spacing) + (font + 12) * OverlayLayout.LineHeightFactor;
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
        Assert.Equal(0, OverlayLayout.FittingLines(21.59, 16, 0));
        Assert.Equal(1, OverlayLayout.FittingLines(21.6, 16, Spacing));
        Assert.Equal(0, OverlayLayout.FittingLines(21.59, 16, Spacing));
    }

    [Fact]
    public void FittingLines_NewestNotLarger_MatchesTheBaseCount()
    {
        Assert.Equal(OverlayLayout.FittingLines(Budget, 16, Spacing), OverlayLayout.FittingLines(Budget, 16, 16, Spacing));
        Assert.Equal(OverlayLayout.FittingLines(Budget, 16, Spacing), OverlayLayout.FittingLines(Budget, 16, 10, Spacing));
    }

    [Fact]
    public void FittingLines_NewestAloneFits_ReturnsOne()
    {
        // The 60 pt newest line (81 DIP) fits, but no 48 pt line fits beside it.
        Assert.Equal(1, OverlayLayout.FittingLines(81, 48, 60, Spacing));
        // Even when the newest itself does not fit, it stays the mandatory floor
        // (the strip clamps to ≥1 and the clip hides the overflow).
        Assert.Equal(1, OverlayLayout.FittingLines(50, 48, 60, Spacing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    public void FittingLines_DegenerateAvailable_ReturnsZero(double available)
    {
        Assert.Equal(0, OverlayLayout.FittingLines(available, 16, Spacing));
        Assert.Equal(0, OverlayLayout.FittingLines(available, 16, 28, Spacing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-16)]
    [InlineData(double.NaN)]
    public void FittingLines_DegenerateFont_ReturnsZero(double font)
    {
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, font, Spacing));
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, font, 28, Spacing));
    }

    [Fact]
    public void FittingLines_DegenerateSpacingOrNewest_ReturnsZero()
    {
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, 16, double.NaN));
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, 16, 28, double.NaN));
        Assert.Equal(0, OverlayLayout.FittingLines(Budget, 16, double.NaN, Spacing));
    }

    [Fact]
    public void FittingLines_AbsurdlyLargeAvailable_Saturates()
    {
        Assert.Equal(int.MaxValue, OverlayLayout.FittingLines(double.MaxValue, 16, Spacing));
        Assert.Equal(int.MaxValue, OverlayLayout.FittingLines(double.MaxValue, 16, 28, Spacing));
    }
}
