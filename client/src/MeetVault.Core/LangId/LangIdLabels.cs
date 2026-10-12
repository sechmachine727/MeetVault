namespace MeetVault.Core;

// Maps the language-ID model's own class labels to the app's language codes. The pinned
// VoxLingua107 export names languages by 2-letter ISO codes (en/ja/vi, class indexes
// 20/45/102 — see scratch/langid-probe/PIN.md); the 3-letter aliases are kept as a
// safety net for future re-exports. Only the shipped transcription languages are
// switchable; everything else maps to null and is ignored by the policy.
public static class LangIdLabels
{
    public static string? ToAppCode(string modelLabel) => modelLabel.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" => "en",
        "ja" or "jpn" => "ja",
        "vi" or "vie" => "vi",
        _ => null,
    };
}
