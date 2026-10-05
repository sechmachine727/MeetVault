namespace MeetVault.Core;

public interface IAudioCapture
{
    Task StartAsync(string micId, string appSource, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    bool IsCapturing { get; }
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
}

