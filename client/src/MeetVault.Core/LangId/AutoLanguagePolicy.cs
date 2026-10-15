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
        string[]? Supported = null);   // defaults to the shipped transcription languages (EngineCatalog)

    private readonly Settings _s;
    private readonly string[] _supported;
    private readonly Queue<string> _recent = new();
    private long? _lastSwitchMs;
    private string? _active;

    // The languages this policy may switch to — the shipped catalog's languages unless the caller
    // pinned an explicit set. Exposed so a test can prove the two cannot drift.
    public IReadOnlyList<string> SupportedLanguages => _supported;

    public AutoLanguagePolicy(Settings? settings = null)
    {
        _s = settings ?? new Settings();
        _supported = _s.Supported ?? MeetVault.Core.EngineCatalog.Languages.Select(l => l.Code).ToArray();
    }

    // A manual pick or resume must clear the cooldown too: detections right after the user's
    // choice must not be silently suppressed (review r6). An auto-applied switch uses the
    // KeepCooldown variant so the anti-thrash window survives the caller's echo.
    public void SetActive(string? active) { _active = active; _recent.Clear(); _lastSwitchMs = null; }
    public void SetActiveKeepCooldown(string? active) { _active = active; _recent.Clear(); }

    // Pause/stop: drop the agreement window and the cooldown so a resume starts clean —
    // prior agreements must not let a single fresh window trigger a switch (review r12).
    public void ResetAgreement() { _recent.Clear(); _lastSwitchMs = null; }

    // The confidence bar for a switch (exposed so the UI's auto-initial pick applies the
    // same bar as the suggestions — review r14).
    public float ConfidenceThreshold => _s.ConfidenceThreshold;

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
