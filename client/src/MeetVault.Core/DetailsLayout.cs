namespace MeetVault.Core;

public enum DetailsLayoutMode { TwoColumn, Wide, Stacked }

// Pure layout decision for the Details workspace, decided in code (never a XAML
// AdaptiveTrigger) so the rule is unit-testable. Tiers by CONTENT size (the
// RootScroll viewport). The height gate (>= 520) binds every multi-column tier;
// the wide tier (owner direction 2026-10-14: the 16:9 video section must be
// the widest section, and three sections are not wanted) is a video-dominant
// TWO-column layout - the media section left and widest plus one transcript +
// analysis rail right - so it needs the width that fits media (>= 560) + the
// rail (>= 480) + spacing and padding (1092) with room for the video to grow.
public static class DetailsLayout
{
    public const double MinTwoColumnWidthDip = 1000;
    public const double MinTwoColumnHeightDip = 520;
    public const double MinWideWidthDip = 1680;

    public static DetailsLayoutMode Decide(double contentWidthDip, double contentHeightDip) =>
        contentWidthDip >= MinWideWidthDip && contentHeightDip >= MinTwoColumnHeightDip
            ? DetailsLayoutMode.Wide
            : contentWidthDip >= MinTwoColumnWidthDip && contentHeightDip >= MinTwoColumnHeightDip
                ? DetailsLayoutMode.TwoColumn
                : DetailsLayoutMode.Stacked;
}
