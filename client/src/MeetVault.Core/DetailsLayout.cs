namespace MeetVault.Core;

public enum DetailsLayoutMode { Wide, Stacked }

// Pure layout decision for the Details workspace, decided in code (never a XAML
// AdaptiveTrigger) so the rule is unit-testable. Tiers by CONTENT size (the
// RootScroll viewport).
//
// Owner direction 2026-10-15: "In smaller screen, the Meeting details UI completely
// breaks. I want to keep the UI like the big screen." The big-screen workspace - the
// video-dominant media region left plus one rail right (Analyze + Summary + Transcript) -
// is therefore used at EVERY content size its two regions fit in, not only on a 1680+
// display: on the owner's 1600x1000 display (1263 DIP of content) and in a windowed
// 1678x754 window (1341 DIP) the workspace is the same as on the 2560x1440 display.
// Below that width the regions cannot fit without clipping, so the page stacks.
public static class DetailsLayout
{
    // The two regions' minimums and the page's own geometry (PageGrid Padding="24",
    // ColumnSpacing="12" in the XAML) - one source of truth, shared with the page.
    public const double MinMediaWidthDip = 560;
    public const double MinRailWidthDip = 480;
    public const double PageSpacingDip = 12;
    public const double PagePaddingDip = 24;
    public const double MinWideHeightDip = 520;

    // The workspace fits - without clipping either region - from exactly this content width up.
    public const double MinWideWidthDip =
        MinMediaWidthDip + MinRailWidthDip + PageSpacingDip + 2 * PagePaddingDip; // 1100

    public static DetailsLayoutMode Decide(double contentWidthDip, double contentHeightDip) =>
        contentWidthDip >= MinWideWidthDip && contentHeightDip >= MinWideHeightDip
            ? DetailsLayoutMode.Wide
            : DetailsLayoutMode.Stacked;
}
