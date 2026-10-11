namespace MeetVault.Core;

// Display-language policy: an explicit ui.language setting (en|ja) wins; otherwise the
// machine's UI language decides — ja* becomes Japanese, everything else English.
// Pure logic so it is testable without WinRT; the app turns the result into
// ApplicationLanguages.PrimaryLanguageOverride.
public static class UiLanguage
{
    public const string SystemDefault = "system";

    // True when the setting is an explicit language choice (en|ja) rather than the
    // system-following default. The App uses this to decide whether the thread CULTURE
    // (date/number formats) follows the resolved UI tag or stays the machine's own.
    public static bool IsExplicit(string? setting)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant();
        return s is "en" or "ja";
    }

    public static string Resolve(string? setting, string systemTag)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant();
        if (IsExplicit(s)) return s;
        return (systemTag ?? "").Trim().ToLowerInvariant().StartsWith("ja") ? "ja" : "en";
    }

    public static string ToTag(string resolved) => resolved == "ja" ? "ja-JP" : "en-US";
}
