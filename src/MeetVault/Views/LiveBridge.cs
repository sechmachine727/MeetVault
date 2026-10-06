namespace MeetVault.Views;

// One tiny hub so the overlay window can mirror the live session without holding a
// reference to the page. Keeps a short backlog so an overlay opened mid-meeting
// still shows recent lines. LivePage publishes lines and owns rename.
public static class LiveBridge
{
    private const int MaxRecent = 8;
    private static readonly List<(string Speaker, string Text)> RecentLines = new();

    public static event Action<string, string>? LineAdded;
    public static event Action? Cleared;
    public static Func<string, string, bool>? RenameByName;   // oldName, newName -> handled

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

    public static bool Rename(string oldName, string newName) => RenameByName?.Invoke(oldName, newName) ?? false;
}

