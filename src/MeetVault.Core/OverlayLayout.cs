namespace MeetVault.Core;

// Pure fitting math for the overlay conversation strip. The strip is a fixed-height
// cell and WinUI does NOT clip a Grid child: an over-tall bottom-anchored panel
// paints over the header and its top lines leave the window, so the code-behind
// caps the shown lines by what actually fits the cell. Plain doubles in/out so
// Core stays free of WinUI/WinRT types (AGENTS.md) and the math is xUnit-testable.
public static class OverlayLayout
{
    // A TextBlock's line box ≈ 1.35 em with WinUI's default line spacing for the
    // strip's fonts at 12..48 pt — measured live on the overlay (a 20 pt title
    // line measures exactly 27 DIP) and used as the per-line height.
    public const double LineHeightFactor = 1.35;

    // Whole lines of fontDip (with lineSpacingDip between them) that fit in
    // availableDip: n such that n*font + (n−1)*spacing ≤ available. The newest line
    // renders larger (latestFontDip) — the strip always shows it, so the older lines
    // only get what remains after its line box. A latestFontDip ≤ fontDip means every
    // line is base size. Returns at least 1 whenever availableDip > 0: the newest line
    // is the strip's mandatory floor (the caller clips anything that still does not fit).
    public static int FittingLines(double availableDip, double fontDip, double latestFontDip, double lineSpacingDip)
    {
        if (!double.IsFinite(availableDip) || !double.IsFinite(fontDip) || !double.IsFinite(latestFontDip) || !double.IsFinite(lineSpacingDip))
            return 0;
        if (availableDip <= 0 || fontDip <= 0)
            return 0;
        var latest = Math.Max(fontDip, latestFontDip);
        var spacing = Math.Max(0, lineSpacingDip);
        var rest = availableDip - latest * LineHeightFactor;
        if (rest < 0) return 1;
        var older = Math.Floor(rest / (fontDip * LineHeightFactor + spacing));
        return older >= int.MaxValue - 1 ? int.MaxValue : 1 + (int)older;
    }
}
