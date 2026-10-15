using Microsoft.Windows.ApplicationModel.Resources;

namespace MeetVault.Views;

// Code-built strings use the same resource map as x:Uid. Implemented over MRT Core
// (Microsoft.Windows.ApplicationModel.Resources) because the L0 probe proved it is the
// only code path that honors the display-language override in the unpackaged exe:
// Windows.ApplicationModel.Resources.ResourceLoader returned "" for the dotted key and
// the English value for the slashed key (both ignoring the override), while MRT Core
// returned the Japanese value. ResourceMap keys live under "Resources/" with
// '/'-separated segments, so the dotted keys ("SetLangRestart") map to
// "Resources/SetLangRestart". A missing key returns the key itself, so a gap is
// visible on screen instead of crashing the page.
public static class Loc
{
    private static ResourceMap? _map;

    private static ResourceMap? Map
    {
        get
        {
            if (_map is null)
            {
                try { _map = new ResourceManager().MainResourceMap; }
                catch { }
            }
            return _map;
        }
    }

    public static string T(string key)
    {
        try
        {
            var value = Map?.GetValue("Resources/" + key.Replace('.', '/'))?.ValueAsString;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch { return key; }
    }

    public static string Tf(string key, params object[] args) => string.Format(T(key), args);
}
