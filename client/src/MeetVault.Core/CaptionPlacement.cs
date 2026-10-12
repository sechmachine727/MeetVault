namespace MeetVault.Core;

// Pure math + formatting for the YouTube-style caption bar. Plain ints and
// strings in/out so Core stays free of WinUI/WinRT types (AGENTS.md: no UI
// types in Core) and the logic is xUnit-testable.
public static class CaptionPlacement
{
    // Bottom-center of a work area (physical pixels), clamped inside it.
    public static (int X, int Y) BottomCenter(
        int areaX, int areaY, int areaW, int areaH,
        int winW, int winH, int marginPx)
    {
        var x = areaX + (areaW - winW) / 2;
        var y = areaY + areaH - winH - marginPx;
        return (Math.Max(areaX, x), Math.Max(areaY, y));
    }

    public static string FormatCaption(string speaker, string text)
        => string.IsNullOrWhiteSpace(speaker) ? (text ?? string.Empty) : $"{speaker.Trim()}: {text ?? string.Empty}";
}
