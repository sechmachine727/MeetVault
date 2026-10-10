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
    public string? MeetingId => _meeting?.Id;
    public bool IsPaused => _paused;
    public bool IsActive => _meeting is not null;
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
        AppLog.Info("session: start " + _meeting.Id + " mic=" + micId);
        Persist(); // durability: draft exists before any crash
        await _audio.StartAsync(micId, appSource, ct);
        Persist(); // link the WAV before the (slow) model load, so a crash still keeps it
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
        AppLog.Info("session: pause " + _meeting.Id);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _paused = false;
        await _audio.StartAsync(_micId, _appSource, ct);
        await _transcriber.StartAsync(ct);
        _meeting = _meeting! with { Status = MeetingStatus.Recording };
        Persist();
        AppLog.Info("session: resume " + _meeting.Id);
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
        AppLog.Info("session: rename " + speakerId + " -> " + next);
    }

    // Stamps the capture's media path onto the meeting so the recording is linked,
    // then saves. The draft is persisted before audio starts, so MediaPath fills in
    // on the next save once the file exists.
    private void Persist()
    {
        if (_meeting is null) return;
        // Link the capture's current file while the meeting is live. Pause/resume keeps one
        // file; a recovered session opens a new one, so the link follows the audio being
        // recorded now. A finished meeting is linked once, to its compacted file, by StopAsync.
        if (_meeting.Status is MeetingStatus.Recording or MeetingStatus.Paused
            && _audio.MediaPath is { } path && path != _meeting.MediaPath)
            _meeting = _meeting with { MediaPath = path, MediaMime = NAudioCapture.ArchiveMime(path) };
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
        AppLog.Info("session: recover " + meetingId + " paused=" + wasPaused + " segments=" + session._segments.Count);
        if (!wasPaused)
        {
            if (string.IsNullOrWhiteSpace(micId))
            {
                session.Dispose();
                throw new ArgumentException("Mic id is required to resume capture.", "micId");
            }
            try
            {
                await audio.StartAsync(micId, appSource, ct);
                await transcriber.StartAsync(ct);
            }
            catch
            {
                // A recovery that fails after capture opened would leave the crashed meeting's
                // file open and still capturing, so release it before the caller gives up.
                session.Dispose();
                try { await audio.StopAsync(ct); } catch { }
                throw;
            }
        }
        return session;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        EnsureActive();
        await _audio.StopAsync(ct);
        await _transcriber.StopAsync(ct);
        await _audio.FinishAsync(ct); // release + compact the recording before it is linked
        _meeting = _meeting! with { Status = MeetingStatus.Ready, EndedAt = DateTimeOffset.Now };
        var kept = _audio.MediaPath;
        if (kept is { Length: > 0 }) _meeting = _meeting! with { MediaPath = kept, MediaMime = NAudioCapture.ArchiveMime(kept) };
        Persist();
        // Remove the working WAV only now that the row points at the compacted file, so a
        // failed save or a crash here still leaves the recording on disk and reachable. The
        // path is derived from the kept file, so a working file that was never linked to the
        // row is cleaned up too.
        if (kept is { Length: > 0 } && kept.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            NAudioCapture.TryDeleteFile(Path.ChangeExtension(kept, ".wav"));
        AppLog.Info("session: stop " + _meeting!.Id + " segments=" + _segments.Count + " media=" + _meeting.MediaPath);
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
        if (speaker is null) { speaker = new Speaker(speakerId, _gender.Label(speakerId, Gender.Unknown)); _speakers.Add(speaker); }
        var seg = raw with { SpeakerId = speakerId };
        _segments.Add(seg);
        AppLog.Debug("session: segment " + speakerId + " [" + seg.StartMs + "-" + seg.EndMs + "] " + seg.Text);
        LiveSegment?.Invoke(seg);
    }

    public void Dispose() => _transcriber.SegmentReady -= OnSegment;

    // Start failed after the draft row was written: mark it failed so it never shows
    // as a finalizable empty meeting.
    public void MarkFailed()
    {
        if (_meeting is null) return;
        AppLog.Warn("session: mark failed " + _meeting.Id);
        _meeting = _meeting with { Status = MeetingStatus.Failed, EndedAt = DateTimeOffset.Now };
        Persist();
        _meeting = null;
    }

    private void EnsureActive() { if (_meeting is null) throw new InvalidOperationException("No active meeting."); }
}






