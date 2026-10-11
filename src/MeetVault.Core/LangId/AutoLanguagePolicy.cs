namespace MeetVault.Core;

// Decides when a detected language should become the active transcription language.
// Conservative by design: a wrong switch costs transcript quality, so require repeated,
// confident agreement on a DIFFERENT language, and never switch again for a cooldown.
public sealed class AutoLanguagePolicy
{
    public sealed record Settings(
        float ConfidenceThreshold = 0.5f,
        int AgreementCount = 3,        // agreeing detections needed to switch
        int AgreementWindow = 5,       // look at the last N detections
        long CooldownMs = 20_000,      // after a switch, ignore suggestions for this long
        string[]? Supported = null);   // defaults to en/ja/vi (the transcription languages)

    private readonly Settings _s;
    private readonly string[] _supported;
    private readonly Queue<string> _recent = new();
    private long? _lastSwitchMs;
    private string? _active;

    public AutoLanguagePolicy(Settings? settings = null)
    { _s = settings ?? new Settings(); _supported = _s.Supported ?? new[] { "en", "ja", "vi" }; }

    public void SetActive(string? active) { _active = active; _recent.Clear(); }

    // A detection arrived (code already mapped to en/ja/vi; conf = detector probability).
    // Returns the language to switch to, or null.
    public string? Detection(string code, float confidence, long nowMs)
    {
        if (confidence < _s.ConfidenceThreshold) return null;
        if (!_supported.Contains(code)) return null;
        if (code == _active) { _recent.Clear(); return null; }
        if (_lastSwitchMs is { } last && nowMs - last < _s.CooldownMs) return null;
        _recent.Enqueue(code);
        while (_recent.Count > _s.AgreementWindow) _recent.Dequeue();
        if (_recent.Count(c => c == code) < _s.AgreementCount) return null;
        _lastSwitchMs = nowMs;
        _recent.Clear();
        return code;
    }
}
