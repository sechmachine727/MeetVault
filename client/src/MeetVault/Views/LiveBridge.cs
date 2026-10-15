namespace MeetVault.Views;

// One tiny hub so the overlay window can mirror the live session without holding a
// reference to the page. Keeps a short backlog so an overlay opened mid-meeting
// still shows recent lines.
public static class LiveBridge
{
    private const int MaxRecent = 8;
    private static readonly List<(string Speaker, string Text)> RecentLines = new();

    public static event Action<string, string>? LineAdded;
    public static event Action? Cleared;
    public static event Action<string>? PartialChanged;       // interim text of the current utterance ("" clears)

    // Whether a recording session is currently open (set by LivePage). The overlay
    // uses it to explain an empty conversation instead of looking silently broken.
    public static bool IsLive { get; private set; }
    public static event Action<bool>? LiveChanged;

    public static void SetLive(bool live)
    {
        if (IsLive == live) return;
        IsLive = live;
        LiveChanged?.Invoke(live);
    }

    public static void Publish(string speaker, string text)
    {
        lock (RecentLines)
        {
            RecentLines.Add((speaker, text));
            if (RecentLines.Count > MaxRecent) RecentLines.RemoveAt(0);
        }
        LineAdded?.Invoke(speaker, text);
    }

    public static IReadOnlyList<(string Speaker, string Text)> Recent()
    {
        lock (RecentLines) return RecentLines.ToArray();
    }

    public static void Clear()
    {
        lock (RecentLines) RecentLines.Clear();
        Cleared?.Invoke();
    }

    // Fired on every streaming partial (and with "" when the open utterance closes).
    public static void NotifyPartial(string text) => PartialChanged?.Invoke(text);

    // --- Meeting lifecycle, language and screen-recording channels. The Live page owns
    // the session and applies changes; every other surface (overlay, settings) only
    // requests — so two controls can never drive the meeting differently. ---

    public enum MeetingState { Idle, Recording, Paused }
    public sealed record LanguageInfo(string Code, bool Auto, bool Detected);

    private static readonly object _stateLock = new();
    public static MeetingState State { get; private set; } = MeetingState.Idle;
    public static LanguageInfo? CurrentLanguage { get; private set; }
    public static bool ScreenRecording { get; private set; }

    public static event Action<MeetingState>? MeetingStateChanged;
    public static event Action<LanguageInfo>? LanguageChanged;
    public static event Action<bool>? ScreenStateChanged;
    public static event Action<string>? LanguageRequested;   // "auto"|"en"|"ja" — from any control
    public static event Action? ScreenToggleRequested;       // from the overlay button

    public static void SetMeetingState(MeetingState state)
    {
        lock (_stateLock) { if (State == state) return; State = state; }
        MeetingStateChanged?.Invoke(state);
    }

    public static void SetLanguage(LanguageInfo info)
    {
        lock (_stateLock) CurrentLanguage = info;
        LanguageChanged?.Invoke(info);
    }

    public static void SetScreenState(bool active)
    {
        lock (_stateLock) { if (ScreenRecording == active) return; ScreenRecording = active; }
        ScreenStateChanged?.Invoke(active);
    }

    public static void RequestLanguage(string code) => LanguageRequested?.Invoke(code);
    public static void RequestScreenToggle() => ScreenToggleRequested?.Invoke();

    // Endonyms are the display convention (English / 日本語); unknown codes echo back.
    public static string LanguageDisplay(string code)
    {
        var hit = Array.Find(MeetVault.Core.EngineCatalog.Languages, l => l.Code == code);
        return hit.Endonym ?? code;
    }
}
