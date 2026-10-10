using SherpaOnnx;

namespace MeetVault.Core;

// One spoken utterance (a sentence as the speaker said it): VAD-closed audio plus its
// position in the recording timeline. Samples are 16 kHz mono floats.
public sealed record Utterance(float[] Samples16k, long StartMs, long EndMs);

// Silero VAD wrapper: feed 16 kHz mono audio, get back utterances that ended at a
// natural pause (silence >= MinSilenceDuration). One utterance = one transcription
// unit = one diarization unit, which is what makes lines sentence-shaped and lets
// the speaker be decided once per sentence instead of once per window.
public sealed class UtteranceSegmenter : IDisposable
{
    private readonly VoiceActivityDetector _vad;
    private long _streamSamples;   // total samples fed since ctor/Reset

    public UtteranceSegmenter(string modelPath, float minSilenceSeconds = 0.5f, float minSpeechSeconds = 0.25f, float maxSpeechSeconds = 15f)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"VAD model not found: {modelPath}.", modelPath);
        var config = new VadModelConfig
        {
            SileroVad = new SileroVadModelConfig
            {
                Model = modelPath,
                Threshold = 0.5f,
                MinSilenceDuration = minSilenceSeconds,
                MinSpeechDuration = minSpeechSeconds,
                MaxSpeechDuration = maxSpeechSeconds,
                WindowSize = 512,
            },
            SampleRate = 16000,
            NumThreads = 1,
            Provider = "cpu",
        };
        _vad = new VoiceActivityDetector(config, 30f);
        AppLog.Info("vad: loaded model=" + Path.GetFileName(modelPath) + " minSilence=" + minSilenceSeconds);
    }

    // Feeds one frame of 16 kHz mono audio; returns utterances closed by this frame.
    public IReadOnlyList<Utterance> Push(float[] samples16k)
    {
        _vad.AcceptWaveform(samples16k);
        _streamSamples += samples16k.Length;
        return Drain();
    }

    // Stream end / pause: close whatever speech is still open.
    public IReadOnlyList<Utterance> Flush()
    {
        _vad.Flush();
        return Drain();
    }

    // New recording segment (after a pause): timing restarts at zero.
    public void Reset()
    {
        _vad.Reset();
        _streamSamples = 0;
    }

    private List<Utterance> Drain()
    {
        var closed = new List<Utterance>();
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front();
            _vad.Pop();
            // seg.Start is in samples, relative to the WHOLE stream fed to the detector.
            // A3 measured this on the committed 278560-sample fixture with 3200-sample
            // chunks: starts 4192 / 97376 / 196192 (the three voice onsets), absolute
            // across 87 Push calls — sherpa keeps segment.start as an index into its
            // stream ring buffer, so no per-push rebasing is needed. (The rejected
            // alternative was _streamSamples - _lastPushSamples + seg.Start.)
            var startSamples = seg.Start;
            var startMs = startSamples * 1000L / 16000;
            var endMs = startMs + seg.Samples.Length * 1000L / 16000;
            closed.Add(new Utterance(seg.Samples, startMs, endMs));
        }
        return closed;
    }

    public void Dispose() => _vad.Dispose();
}
