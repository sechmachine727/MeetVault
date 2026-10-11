namespace MeetVault.Core;

// Text shaping shared by the engines: the non-speech filter for whisper.cpp's silence
// markers plus the sentence-case normalizer for the case-less streaming exports.
public static class SpeechText
{
    public static bool IsNonSpeech(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var normalized = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "blankaudio" or "silence" or "inaudible";
    }

    // The streaming exports are case-less: the zipformer 20M vocabulary is ALL CAPS and
    // Vosk emits all lowercase, and neither writes sentence punctuation. Normalize to
    // sentence case for display/export: lowercase everything, capitalize the first letter
    // (only when nothing alphanumeric precedes it, so "20 minutes" stays untouched), and
    // restore the English pronoun "I" (standalone and in 'm/'ve/'ll/'d contractions).
    // Per-utterance engines (whisper, moonshine) already return real casing and must
    // never pass through this.
    public static string SentenceCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var chars = text.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetter(chars[i])) continue; // the first letter wins
            var startsSentence = true;
            for (var j = 0; j < i; j++)
                if (char.IsLetterOrDigit(chars[j])) { startsSentence = false; break; }
            if (startsSentence) chars[i] = char.ToUpperInvariant(chars[i]);
            break;
        }
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] == 'i'
                && (i == 0 || !char.IsLetterOrDigit(chars[i - 1]))
                && (i + 1 >= chars.Length || !char.IsLetterOrDigit(chars[i + 1])))
                chars[i] = 'I';
        return new string(chars);
    }
}
