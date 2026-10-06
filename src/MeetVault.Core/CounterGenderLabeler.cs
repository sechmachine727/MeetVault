namespace MeetVault.Core;

// Bridges per-turn gender estimates to the counter-based display names (Male N / Female N / Unknown).
// ponytail: estimate source is a Func until a validated classifier lands behind IGenderEstimator.
public sealed class CounterGenderLabeler : IGenderLabeler
{
    private readonly SpeakerLabeler _counters = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly Func<string, Gender> _estimate;

    public CounterGenderLabeler(Func<string, Gender>? estimate = null) => _estimate = estimate ?? (_ => Gender.Unknown);

    public string Label(string speakerId, Gender estimate)
    {
        if (_names.TryGetValue(speakerId, out var existing)) return existing;
        var decided = _estimate(speakerId);
        if (decided == Gender.Unknown) decided = estimate;
        var name = _counters.Assign(decided);
        _names[speakerId] = name;
        return name;
    }
}

// Gender labels off: stable neutral names, no estimates consumed.
public sealed class NeutralSpeakerLabeler : IGenderLabeler
{
    private readonly Dictionary<string, string> _names = new();
    private int _count;

    public string Label(string speakerId, Gender estimate)
    {
        if (_names.TryGetValue(speakerId, out var existing)) return existing;
        var name = "Speaker " + (++_count);
        _names[speakerId] = name;
        return name;
    }
}
