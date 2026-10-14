namespace MeetVault.Core;

public enum DetailsLayoutMode { TwoColumn, ThreeColumn, Stacked }

// Pure layout decision for the Details workspace, decided in code (never a XAML
// AdaptiveTrigger) so the rule is unit-testable. Tiers by CONTENT size (the
// RootScroll viewport). The height gate (>= 520) binds every multi-column tier;
// the three-column tier additionally needs the width that fits media (>= 560) +
// transcript (>= 440) + analysis (>= 520) side by side (plus 2x12 spacing and
// 2x24 grid padding = 1592), so large screens read horizontally in three
// columns instead of one tall capped stack.
public static class DetailsLayout
{
    public const double MinTwoColumnWidthDip = 1000;
    public const double MinTwoColumnHeightDip = 520;
    public const double MinThreeColumnWidthDip = 1680;

    public static DetailsLayoutMode Decide(double contentWidthDip, double contentHeightDip) =>
        contentWidthDip >= MinThreeColumnWidthDip && contentHeightDip >= MinTwoColumnHeightDip
            ? DetailsLayoutMode.ThreeColumn
            : contentWidthDip >= MinTwoColumnWidthDip && contentHeightDip >= MinTwoColumnHeightDip
                ? DetailsLayoutMode.TwoColumn
                : DetailsLayoutMode.Stacked;
}
