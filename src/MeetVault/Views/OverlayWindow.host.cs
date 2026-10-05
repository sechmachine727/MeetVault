namespace MeetVault.Views;

public sealed partial class OverlayWindow
{
    private static OverlayWindow? _current;

    public static void ShowOrActivate()
    {
        if (_current is null) { _current = new OverlayWindow(); _current.Closed += (_, _) => _current = null; }
        _current.Activate();
    }
}

