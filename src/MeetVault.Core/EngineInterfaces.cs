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

// A transcriber that processes one finalized utterance at a time (the VAD-utterance
// pipeline). Whisper and Moonshine implement this; streaming engines do not.
public interface IUtteranceTranscriber : ISpeechTranscriber
{
    Task TranscribeWindowAsync(byte[] pcm16kMono, long windowStartMs, string meetingId, CancellationToken ct = default);
}

// A transcriber that consumes the live 16 kHz stream continuously and emits interim
// text plus finalized utterances (endpointed by the engine). Push is called from the
// capture pump thread and MUST only enqueue; decoding happens on the engine's own thread.
public interface IStreamingTranscriber : ISpeechTranscriber, IDisposable
{
    event Action<string>? PartialReady; // full interim text of the open utterance; "" clears it
    void Push(float[] samples16k);
    void Flush(); // close pending speech (pause/stop/unload); the stream stays usable
}

// The engine-specific slice of a streaming recognizer. ALL calls happen on the runner's
// single decode thread (no locking needed).
public interface IStreamingBackend : IDisposable
{
    void AcceptWaveform(float[] samples16k);
    bool IsReady();                     // ready to decode more frames
    void Decode();                      // decode one step
    string CurrentText();               // interim text of the open utterance ("" when none)
    bool TryTakeFinal(out string text); // the engine closed an utterance (endpoint); resets it
    bool TryFlushFinal(out string text);// the CALLER closes it now (pause/stop); resets it
}

// Where a speaker id came from. Asserted as mint-origin evidence by the contract
// tests (DiarizerContractTests, SpeakerMatcherTests); keep the values meaningful.
public enum SpeakerOrigin { Manual, SourceHint, Embedding, Unknown }

// One finalized turn's audio (16 kHz mono) and its position in the recording timeline.
public sealed record TurnAudio(ReadOnlyMemory<float> Samples16kMono, long StartMs, long EndMs);

// A stable speaker association for one turn. SpeakerId is opaque and stable for the
// meeting ("spk_1", ...); an empty SpeakerId means no association (Origin Unknown).
// Display names live in the repository rename map, never here.
public sealed record SpeakerHint(string SpeakerId, SpeakerOrigin Origin);

public interface ISpeakerDiarizer
{
    // Assigns a stable speaker for one finalized turn. Must never throw for valid
    // input; empty audio returns an Unknown hint instead of throwing.
    SpeakerHint AssignTurn(TurnAudio turn);

    // Recovery hook: hands the persisted draft rows to the diarizer before capture
    // restarts, so a recovered meeting continues its spk_N id space instead of minting
    // ids that collide with the persisted rows (see SpeakerMatcher.SeedForRecovery).
    // Called ONLY by MeetingSession.RecoverAsync — never for a new meeting (that path
    // resets instead, via the concrete diarizer's Reset). Default no-op so simple and
    // fake diarizers need no change.
    void SeedForRecovery(IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments) { }
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


