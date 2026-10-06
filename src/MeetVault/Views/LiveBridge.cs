namespace MeetVault.Views;

// One tiny hub so the overlay window can mirror the live session without holding a
// reference to the page. LivePage publishes lines and owns rename; the overlay
// subscribes and forwards rename requests back.
public static class LiveBridge
{
    public static event Action<string, string>? LineAdded;   // displayName, text
    public static event Action? Cleared;
    public static Func<string, string, bool>? RenameByName;   // oldName, newName -> handled

    public static void Publish(string speaker, string text) => LineAdded?.Invoke(speaker, text);
    public static void Clear() => Cleared?.Invoke();
    public static bool Rename(string oldName, string newName) => RenameByName?.Invoke(oldName, newName) ?? false;
}

