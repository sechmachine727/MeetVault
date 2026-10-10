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

public enum SpeakerOrigin { Manual, SourceHint, Embedding, Unknown }

// One finalized turn's audio (16 kHz mono) and its position in the recording timeline.
public sealed record TurnAudio(ReadOnlyMemory<float> Samples16kMono, long StartMs, long EndMs);

// A stable speaker association for one turn. SpeakerId is opaque and stable for the
// meeting ("spk_1", ...); an empty SpeakerId means no association (Origin Unknown).
// Confidence is null when the engine reports no score. Display names live in the
// repository rename map, never here.
public sealed record SpeakerHint(string SpeakerId, SpeakerOrigin Origin, float? Confidence);

public interface ISpeakerDiarizer
{
    // Assigns a stable speaker for one finalized turn. Must never throw for valid
    // input; empty audio returns an Unknown hint instead of throwing.
    SpeakerHint AssignTurn(TurnAudio turn);
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


