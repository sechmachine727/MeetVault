using SherpaOnnx;

namespace MeetVault.Core;

// Per-utterance engine: one silero utterance in, one or more sherpa-onnx offline decodes
// out. Used for Moonshine (v2 quantized: encoder_model.ort + decoder_model_merged.ort +
// tokens.txt). The VAD + diarization pipeline around it is unchanged from whisper.
public sealed class SherpaOfflineTranscriber : IUtteranceTranscriber, IDisposable
{
    // Moonshine v2 (2026-02-27) quantized exports fail beyond ~9.2 s per decode (ORT
    // broadcast error in the decoder attention -> empty result; measured on tiny-en and
    // tiny-ja). Longer VAD utterances are split at quiet points well under that limit.
    internal const int MaxChunkMs = 8000;

    private readonly OfflineRecognizer _recognizer;
    private readonly string _name;

    public event Action<Segment>? SegmentReady;
    public bool IsRunning { get; private set; }

    public SherpaOfflineTranscriber(string modelDir, string name = "moonshine")
    {
        _name = name;
        var config = new OfflineRecognizerConfig();
        config.FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 };
        config.ModelConfig.Moonshine = new OfflineMoonshineModelConfig
        {
            Encoder = Path.Combine(modelDir, "encoder_model.ort"),
            MergedDecoder = Path.Combine(modelDir, "decoder_model_merged.ort"),
        };
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        _recognizer = new OfflineRecognizer(config);
        AppLog.Info("moonshine: loaded " + name + " dir=" + modelDir);
    }

    public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }

    public Task TranscribeWindowAsync(byte[] pcm16kMono, long windowStartMs, string meetingId, CancellationToken ct = default)
    {
        if (!IsRunning || pcm16kMono.Length < 2) return Task.CompletedTask;
        var floats = Pcm16.ToFloats(pcm16kMono);
        var windows = AudioWindows.Split(floats, MaxChunkMs);
        var index = 0;
        for (var i = 0; i < windows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var w = windows[i];
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, floats.AsSpan(w.OffsetSamples, w.LengthSamples).ToArray());
            _recognizer.Decode(stream);
            var text = stream.Result.Text.Trim();
            var chunkMs = w.LengthSamples / 16;
            AppLog.Debug("moonshine: window " + (chunkMs / 1000.0).ToString("0.0") + "s -> "
                + (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds + "ms text=" + text.Length
                + (windows.Count > 1 ? " (" + (i + 1) + "/" + windows.Count + ")" : ""));
            if (text.Length == 0 || SpeechText.IsNonSpeech(text)) continue;
            var startMs = windowStartMs + w.OffsetSamples / 16;
            var endMs = windowStartMs + (w.OffsetSamples + w.LengthSamples) / 16;
            SegmentReady?.Invoke(new Segment(meetingId + "-m" + startMs + "-" + index++, "", startMs, endMs, text, true));
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _recognizer.Dispose();
}
