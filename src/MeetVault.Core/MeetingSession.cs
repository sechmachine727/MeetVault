namespace MeetVault.Core;

// ponytail: single-threaded session; thread-safety add when real engines stream concurrently.
public sealed class MeetingSession
{
    private readonly IAudioCapture _audio;
    private readonly ISpeechTranscriber _transcriber;
    private readonly ISpeakerDiarizer _diarizer;
    private readonly IGenderLabeler _gender;
    private readonly IMeetingStore _store;
    private readonly List<Segment> _segments = new();
    private readonly List<Speaker> _speakers = new();
    private Meeting? _meeting;
    private bool _paused;
    private string _micId = "";
    private string _appSource = "";

    public IReadOnlyList<Segment> Segments => _segments;
    public IReadOnlyList<Speaker> Speakers => _speakers;
    public event Action<Segment>? LiveSegment;

    public MeetingSession(IAudioCapture audio, ISpeechTranscriber transcriber, ISpeakerDiarizer diarizer, IGenderLabeler gender, IMeetingStore store)
    {
        _audio = audio; _transcriber = transcriber; _diarizer = diarizer; _gender = gender; _store = store;
        _transcriber.SegmentReady += OnSegment;
    }

    public async Task StartAsync(string micId, string appSource, CancellationToken ct = default)
    {
        if (_meeting is { Status: MeetingStatus.Recording or MeetingStatus.Paused }) throw new InvalidOperationException("Already started.");
        _meeting = new Meeting(Guid.NewGuid().ToString("n"), "Meeting " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), DateTimeOffset.Now, null, MeetingStatus.Recording, null, null);
        _paused = false;
        _micId = micId; _appSource = appSource; // retained so Resume restarts the same sources
        await _audio.StartAsync(micId, appSource, ct);
        await _transcriber.StartAsync(ct);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _paused = true;
        await _audio.StopAsync(ct);
        await _transcriber.StopAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Paused };
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _paused = false;
        await _audio.StartAsync(_micId, _appSource, ct);
        await _transcriber.StartAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Recording };
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        EnsureActive();
        await _audio.StopAsync(ct);
        await _transcriber.StopAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Ready, EndedAt = DateTimeOffset.Now };
        _store.Save(_meeting, _speakers, _segments);
        _meeting = null;
    }

    private void OnSegment(Segment raw)
    {
        if (_paused || _meeting is null) return; // gap: paused audio never reappears
        var speakerId = _diarizer.AssignTurn(raw);
        var speaker = _speakers.FirstOrDefault(s => s.Id == speakerId);
        if (speaker is null) { speaker = new Speaker(speakerId, _gender.Label(speakerId, Gender.Unknown), Gender.Unknown, 0); _speakers.Add(speaker); }
        var seg = raw with { SpeakerId = speakerId };
        _segments.Add(seg);
        LiveSegment?.Invoke(seg);
    }

    private void EnsureActive() { if (_meeting is null) throw new InvalidOperationException("No active meeting."); }
}

