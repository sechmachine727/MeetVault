namespace MeetVault.Core;

// Non-speech text filter shared by the per-utterance engines. whisper.cpp emits bracketed
// silence markers ([BLANK_AUDIO], [Silence], (inaudible)) on quiet windows; they are not
// speech and must not become transcript lines. Public so moonshine applies the same rule.
public static class SpeechText
{
    public static bool IsNonSpeech(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var normalized = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "blankaudio" or "silence" or "inaudible";
    }
}
