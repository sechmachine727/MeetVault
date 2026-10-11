namespace MeetVault.Core;

// Text shaping for the recognizer output: sentence-case normalization for the case-less
// streaming export (see SentenceCase below).
public static class SpeechText
{
    // Vosk's small models emit all-lowercase text with no sentence punctuation.
    // Normalize to sentence case for display/export: lowercase everything, capitalize the
    // first letter (only when nothing alphanumeric precedes it, so "20 minutes" stays
    // untouched), and restore the English pronoun "I" (standalone and in 'm/'ve/'ll/'d
    // contractions).
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
