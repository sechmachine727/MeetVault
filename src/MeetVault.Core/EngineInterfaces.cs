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

// The transcriber consumes the live 16 kHz stream continuously and emits interim text
// plus finalized utterances (endpointed by the engine). Push is called from the capture
// pump thread and MUST only enqueue; decoding happens on the engine's own thread.
public interface IStreamingTranscriber : ISpeechTranscriber, IDisposable
{
    event Action<string>? PartialReady; // full interim text of the open utterance; "" clears it
    void Push(float[] samples16k);
}

// The engine-specific slice of a streaming recognizer. ALL calls happen on the runner's
// single decode thread (no locking needed).
public interface IStreamingBackend : IDisposable
{
    void AcceptWaveform(float[] samples16k);
    string CurrentText();               // interim text of the open utterance ("" when none)
    bool TryTakeFinal(out string text); // the engine closed an utterance (endpoint); resets it
    bool TryFlushFinal(out string text);// the CALLER closes it now (pause/stop); resets it
}

public interface IMeetingStore
{
    void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments);
    IReadOnlyList<Segment> LoadSegments(string meetingId);
    IReadOnlyList<Speaker> LoadSpeakers(string meetingId);
    IReadOnlyList<Meeting> LoadUnfinished();
}
