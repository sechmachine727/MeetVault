namespace MeetVault.Views;

public sealed partial class OverlayWindow
{
    private static OverlayWindow? _current;

    public static void ShowOrActivate()
    {
        if (_current is null) { _current = new OverlayWindow(); _current.Closed += (_, _) => _current = null; }
        _current.Activate();
    }

    // Auto-lifecycle: the recording-stopped path closes the overlay that was auto-opened.
    public static void CloseIfOpen()
    {
        var w = _current;
        if (w is null) return;
        try { w.Close(); } catch { }
    }
}

