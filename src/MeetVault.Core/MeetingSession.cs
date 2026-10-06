namespace MeetVault.Core;

// ponytail: single-threaded session; thread-safety add when real engines stream concurrently.
public sealed class MeetingSession : IDisposable
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
    // Captured at construction (UI thread in the app); null in tests, where we run inline.
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

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
if (string.IsNullOrWhiteSpace(micId)) throw new ArgumentException("Mic id is required.", "micId");
        if (_meeting is { Status: MeetingStatus.Recording or MeetingStatus.Paused }) throw new InvalidOperationException("Already started.");
        _meeting = new Meeting(Guid.NewGuid().ToString("n"), "Meeting " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), DateTimeOffset.Now, null, MeetingStatus.Recording, null, null);
        _paused = false;
        _micId = micId; _appSource = appSource; // retained so Resume restarts the same sources
        Persist(); // durability: draft exists before any crash
        await _audio.StartAsync(micId, appSource, ct);
        await _transcriber.StartAsync(ct);
        Persist(); // capture path now exists
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _paused = true;
        await _audio.StopAsync(ct);
        await _transcriber.StopAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Paused };
        Persist();
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _paused = false;
        await _audio.StartAsync(_micId, _appSource, ct);
        await _transcriber.StartAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Recording };
        Persist();
    }

    public Task CheckpointAsync(CancellationToken ct = default)
    {
        EnsureActive();
        Persist();
        return Task.CompletedTask;
    }

    // FR2: renaming a speaker updates every row and persists with the draft.
    public void RenameSpeaker(string speakerId, string displayName)
    {
        var next = displayName?.Trim() ?? "";
        if (next.Length == 0) return;
        var i = _speakers.FindIndex(s => s.Id == speakerId);
        if (i < 0) return;
        _speakers[i] = _speakers[i].Rename(next);
        if (_meeting is not null) Persist();
    }

    // Stamps the capture's media path onto the meeting so the recording is linked,
    // then saves. The draft is persisted before audio starts, so MediaPath fills in
    // on the next save once the file exists.
    private void Persist()
    {
        if (_meeting is null) return;
        if (_meeting.MediaPath is null && _audio.MediaPath is { } path)
            _meeting = _meeting with { MediaPath = path, MediaMime = "audio/wav" };
        _store.Save(_meeting, _speakers, _segments);
    }

    public static async Task<MeetingSession> RecoverAsync(string meetingId, IAudioCapture audio, ISpeechTranscriber transcriber, ISpeakerDiarizer diarizer, IGenderLabeler gender, IMeetingStore store, string micId = "", string appSource = "", CancellationToken ct = default)
    {
        var unfinished = store.LoadUnfinished().FirstOrDefault(m => m.Id == meetingId) ?? throw new InvalidOperationException("No recoverable draft for this meeting.");
        var session = new MeetingSession(audio, transcriber, diarizer, gender, store);
        var wasPaused = unfinished.Status == MeetingStatus.Paused;
        session._meeting = unfinished with { Status = wasPaused ? MeetingStatus.Paused : MeetingStatus.Recording };
        session._paused = wasPaused;
        session._speakers.AddRange(store.LoadSpeakers(meetingId));
        session._segments.AddRange(store.LoadSegments(meetingId));
        session._micId = micId; session._appSource = appSource;
        if (!wasPaused)
        {
            if (string.IsNullOrWhiteSpace(micId)) throw new ArgumentException("Mic id is required to resume capture.", "micId");
            await audio.StartAsync(micId, appSource, ct);
            await transcriber.StartAsync(ct);
        }
        return session;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        EnsureActive();
        await _audio.StopAsync(ct);
        await _transcriber.StopAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Ready, EndedAt = DateTimeOffset.Now };
        Persist();
        _meeting = null;
    }

    private void OnSegment(Segment raw)
    {
        // Whisper/diarizer callbacks arrive on a worker thread; marshal onto the UI
        // thread so the collections are never mutated while the UI enumerates them.
        if (_ui is not null && _ui != SynchronizationContext.Current)
        {
            _ui.Post(_ => HandleSegment(raw), null);
            return;
        }
        HandleSegment(raw);
    }

    private void HandleSegment(Segment raw)
    {
        if (_paused || _meeting is null) return; // gap: paused audio never reappears
        var speakerId = _diarizer.AssignTurn(raw);
        var speaker = _speakers.FirstOrDefault(s => s.Id == speakerId);
        if (speaker is null) { speaker = new Speaker(speakerId, _gender.Label(speakerId, Gender.Unknown), Gender.Unknown, 0); _speakers.Add(speaker); }
        var seg = raw with { SpeakerId = speakerId };
        _segments.Add(seg);
        LiveSegment?.Invoke(seg);
    }

    public void Dispose() => _transcriber.SegmentReady -= OnSegment;

    // Start failed after the draft row was written: mark it failed so it never shows
    // as a finalizable empty meeting.
    public void MarkFailed()
    {
        if (_meeting is null) return;
        _meeting = _meeting with { Status = MeetingStatus.Failed, EndedAt = DateTimeOffset.Now };
        Persist();
        _meeting = null;
    }

    private void EnsureActive() { if (_meeting is null) throw new InvalidOperationException("No active meeting."); }
}






