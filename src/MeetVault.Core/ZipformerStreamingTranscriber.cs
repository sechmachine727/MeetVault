using SherpaOnnx;

namespace MeetVault.Core;

// Streaming engine: sherpa-onnx OnlineRecognizer (streaming zipformer transducer).
// Endpointing rules: close 2 s after silence when nothing decoded, 1 s after speech,
// hard cap 15 s per utterance so a monologue still lands in pieces.
public sealed class ZipformerStreamingTranscriber : IStreamingTranscriber
{
    private readonly ZipformerStreamingBackend _backend;
    private readonly StreamingRunner _runner;

    public ZipformerStreamingTranscriber(string modelDir, Func<long>? nowMs = null)
    {
        _backend = new ZipformerStreamingBackend(modelDir);
        _runner = new StreamingRunner(_backend, nowMs);
        AppLog.Info("zipformer: loaded dir=" + modelDir);
    }

    public bool IsRunning { get; private set; }
    public event Action<Segment>? SegmentReady { add { _runner.SegmentReady += value; } remove { _runner.SegmentReady -= value; } }
    public event Action<string>? PartialReady { add { _runner.PartialReady += value; } remove { _runner.PartialReady -= value; } }

    public Task StartAsync(CancellationToken ct)
    {
        // A start while already running (unpaired Start/Resume) closes the previous decode
        // thread instead of leaking it; the common start/stop pairing is unaffected.
        if (IsRunning) _runner.Flush();
        // Prime BEFORE the decode thread starts (no shared-state race): speech landing in
        // the first ~2 s of the stream otherwise loses its head.
        _backend.Prime();
        _runner.Start();
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _runner.Flush(); IsRunning = false; return Task.CompletedTask; }
    public void Push(float[] samples16k) => _runner.Push(samples16k);
    public void Flush() => _runner.Flush();
    public void Dispose() { IsRunning = false; _runner.Dispose(); }
}

// The sherpa-onnx slice of the zipformer transducer stream. One instance = one meeting.
public sealed class ZipformerStreamingBackend : IStreamingBackend
{
    // The streaming zipformer loses the head of speech that lands in the first ~2 s of a
    // stream (the model's onset context is not established yet — measured here: the first
    // 1-2 s of a file decodes as a fragment or garbles, both int8 and float, any chunk
    // shape; the official dotnet example primes with 0.3 s, which measured too short).
    // 1.5 s of leading silence (chunk-shaped, matching the live feed) keeps the first
    // utterance's head intact on every measured run. Below Rule1MinTrailingSilence so no
    // early endpoint fires inside the preroll.
    private const int PrerollMs = 1500;

    private readonly OnlineRecognizer _recognizer;
    private readonly OnlineStream _stream;

    // Must be called BEFORE the decode thread consumes any real audio (the stream is not
    // thread-safe); the wrapper calls it with the runner stopped.
    public void Prime()
    {
        var silence = new float[16 * PrerollMs];
        for (var i = 0; i < silence.Length; i += 160) _stream.AcceptWaveform(16000, silence.AsSpan(i, 160).ToArray());
    }

    public ZipformerStreamingBackend(string modelDir)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 };
        config.ModelConfig.Transducer = new OnlineTransducerModelConfig
        {
            Encoder = Path.Combine(modelDir, "encoder-epoch-99-avg-1.int8.onnx"),
            Decoder = Path.Combine(modelDir, "decoder-epoch-99-avg-1.int8.onnx"),
            Joiner = Path.Combine(modelDir, "joiner-epoch-99-avg-1.int8.onnx"),
        };
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        // ModelType stays empty: sherpa auto-detects zipformer2 from the file names (the
        // official docs use no --model-type flag for this model). If loading ever errors
        // with "unknown model type", set config.ModelConfig.ModelType = "zipformer2".
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.0f;
        config.Rule2MinTrailingSilence = 1.0f;
        config.Rule3MinUtteranceLength = 15.0f;
        _recognizer = new OnlineRecognizer(config);
        _stream = _recognizer.CreateStream();
    }

    public void AcceptWaveform(float[] samples16k) => _stream.AcceptWaveform(16000, samples16k);
    public bool IsReady() => _recognizer.IsReady(_stream);
    public void Decode() => _recognizer.Decode(_stream);
    public string CurrentText() => _recognizer.GetResult(_stream).Text.Trim();

    public bool TryTakeFinal(out string text)
    {
        text = "";
        if (!_recognizer.IsEndpoint(_stream)) return false;
        text = _recognizer.GetResult(_stream).Text.Trim();
        _recognizer.Reset(_stream);
        return true;
    }

    public bool TryFlushFinal(out string text)
    {
        text = _recognizer.GetResult(_stream).Text.Trim();
        _recognizer.Reset(_stream);
        return text.Length > 0;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _recognizer.Dispose();
    }
}
