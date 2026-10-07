namespace MeetVault.Core;

public interface IAudioCapture
{
    Task StartAsync(string micId, string appSource, CancellationToken ct);
    // Pauses capture. The recording stays resumable: StartAsync reopens the same file.
    Task StopAsync(CancellationToken ct);
    // The meeting is over. Stops capture, releases the working file and compacts it to the
    // stored format, so a paused meeting never masquerades as a finished one.
    Task FinishAsync(CancellationToken ct);
    bool IsCapturing { get; }
    string? MediaPath { get; }
}

public interface ISpeechTranscriber
{
    event Action<Segment>? SegmentReady;
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    bool IsRunning { get; }
}

public interface ISpeakerDiarizer
{
    string AssignTurn(Segment segment);
}

public interface IGenderLabeler
{
    string Label(string speakerId, Gender estimate);
}

public interface IMeetingStore
{
    void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments);
    IReadOnlyList<Segment> LoadSegments(string meetingId);
    IReadOnlyList<Speaker> LoadSpeakers(string meetingId);
    IReadOnlyList<Meeting> LoadUnfinished();
}


