namespace MeetVault.Core;

// Display-language policy: an explicit ui.language setting (en|ja) wins; otherwise the
// machine's UI language decides — ja* becomes Japanese, everything else English.
// Pure logic so it is testable without WinRT; the app turns the result into
// ApplicationLanguages.PrimaryLanguageOverride.
public static class UiLanguage
{
    public const string SystemDefault = "system";

    public static string Resolve(string? setting, string systemTag)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant();
        if (s is "en" or "ja") return s;
        return (systemTag ?? "").Trim().ToLowerInvariant().StartsWith("ja") ? "ja" : "en";
    }

    public static string ToTag(string resolved) => resolved == "ja" ? "ja-JP" : "en-US";
}
