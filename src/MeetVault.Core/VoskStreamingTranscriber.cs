using System.Text.Json;
using Vosk;

namespace MeetVault.Core;

// Streaming engine: Vosk small (Kaldi) — very light, partial results, best on old CPUs.
public sealed class VoskStreamingTranscriber : IStreamingTranscriber
{
    private readonly StreamingRunner _runner;

    public VoskStreamingTranscriber(string modelDir, Func<long>? nowMs = null)
    {
        _runner = new StreamingRunner(new VoskStreamingBackend(modelDir), nowMs);
        AppLog.Info("vosk: loaded dir=" + modelDir);
    }

    public bool IsRunning { get; private set; }
    public event Action<Segment>? SegmentReady { add { _runner.SegmentReady += value; } remove { _runner.SegmentReady -= value; } }
    public event Action<string>? PartialReady { add { _runner.PartialReady += value; } remove { _runner.PartialReady -= value; } }

    public Task StartAsync(CancellationToken ct)
    {
        // A start while already running (unpaired Start/Resume) closes the previous decode
        // thread instead of leaking it; the common start/stop pairing is unaffected.
        if (IsRunning) _runner.Flush();
        _runner.Start();
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _runner.Flush(); IsRunning = false; return Task.CompletedTask; }
    public void Push(float[] samples16k) => _runner.Push(samples16k);
    public void Flush() => _runner.Flush();
    public void Dispose() { IsRunning = false; _runner.Dispose(); }
}

public static class VoskJson
{
    public static string Text(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) ? (v.GetString() ?? "").Trim() : "";
        }
        catch { return ""; }
    }
}

// The Vosk slice of the stream. AcceptWaveform returns true when Vosk itself finalized
// an utterance (silence endpoint); Result() then holds it and the recognizer continues.
public sealed class VoskStreamingBackend : IStreamingBackend
{
    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private string? _pendingFinal;

    public VoskStreamingBackend(string modelDir)
    {
        _model = new Model(modelDir);
        _recognizer = new VoskRecognizer(_model, 16000.0f);
        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(false);
    }

    public void AcceptWaveform(float[] samples16k)
    {
        var bytes = Pcm16.FromFloats(samples16k);
        if (_recognizer.AcceptWaveform(bytes, bytes.Length))
            _pendingFinal = SpeechText.SentenceCase(VoskJson.Text(_recognizer.Result(), "text"));
    }

    // Vosk decodes inside AcceptWaveform; there is no separate drain step (a `true` here
    // would spin the runner's decode loop forever).
    public bool IsReady() => false;
    public void Decode() { }
    // Vosk's small models emit all-lowercase, unpunctuated text; sentence case restores
    // the first letter (same rule as the zipformer above).
    public string CurrentText() => SpeechText.SentenceCase(VoskJson.Text(_recognizer.PartialResult(), "partial"));

    public bool TryTakeFinal(out string text)
    {
        text = _pendingFinal ?? "";
        var had = _pendingFinal is not null;
        _pendingFinal = null;
        return had;
    }

    public bool TryFlushFinal(out string text)
    {
        text = SpeechText.SentenceCase(VoskJson.Text(_recognizer.FinalResult(), "text"));
        return text.Length > 0;
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _model.Dispose();
    }
}
