namespace MeetVault.Core;

public enum DetailsLayoutMode { TwoColumn, Stacked }

// Pure layout decision for the Details workspace: two columns only when both the content
// width and the content height can hold the left region and the transcript side by side.
// Decided in code (never a XAML AdaptiveTrigger) so the rule is unit-testable.
public static class DetailsLayout
{
    public const double MinTwoColumnWidthDip = 1000;
    public const double MinTwoColumnHeightDip = 520;

    public static DetailsLayoutMode Decide(double contentWidthDip, double contentHeightDip) =>
        contentWidthDip >= MinTwoColumnWidthDip && contentHeightDip >= MinTwoColumnHeightDip
            ? DetailsLayoutMode.TwoColumn
            : DetailsLayoutMode.Stacked;
}
