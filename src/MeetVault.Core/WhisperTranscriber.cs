using Whisper.net;
using Whisper.net.Ggml;

namespace MeetVault.Core;

// Real transcription behind ISpeechTranscriber: whisper.cpp v1.9.4 via Whisper.net 1.9.1.
// ponytail: file-based finalized windows; streaming partials arrive with the VAD sliding-window upgrade.
public sealed class WhisperTranscriber : ISpeechTranscriber, IDisposable
{
    public event Action<Segment>? SegmentReady;
    public bool IsRunning { get; private set; }

    private readonly string _modelPath;
    private readonly string _language; // "auto" lets whisper detect EN/JA/VI per segment, including mid-conversation switches
    private WhisperFactory? _factory;
    private CancellationTokenSource? _cts;

    public WhisperTranscriber(string modelPath, string language = "auto")
    {
        _modelPath = modelPath;
        _language = language;
    }

    public static async Task DownloadModelAsync(string destPath, GgmlType type, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, QuantizationType.NoQuantization, ct);
        using var fileWriter = File.OpenWrite(destPath);
        await modelStream.CopyToAsync(fileWriter, ct);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _factory ??= WhisperFactory.FromPath(_modelPath);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        IsRunning = false;
        return Task.CompletedTask;
    }

    public event Action<string, float>? LanguageDetected; // per-window detected language + probability (EN/JA/VI switching)

    // Transcribe one finalized 16 kHz mono PCM window; emits one Segment per whisper segment.
    public async Task TranscribeWindowAsync(byte[] pcm16kMono, long windowStartMs, string meetingId, CancellationToken ct = default)
    {
        if (_factory is null) throw new InvalidOperationException("Transcriber not started.");
        using var processor = _factory.CreateBuilder().WithLanguage(_language).Build();
        var floats = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < floats.Length; i++) floats[i] = BitConverter.ToInt16(pcm16kMono, 2 * i) / 32768f;
        var (lang, prob) = processor.DetectLanguageWithProbability(floats, ["en", "ja", "vi"]);
        LanguageDetected?.Invoke(lang ?? "unknown", prob);
        using var stream = new MemoryStream(ToWav16kMono(pcm16kMono));
        var index = 0;
        await foreach (var result in processor.ProcessAsync(stream, ct))
            SegmentReady?.Invoke(new Segment($"{meetingId}-w{windowStartMs}-{index++}", "", windowStartMs + (long)result.Start.TotalMilliseconds, windowStartMs + (long)result.End.TotalMilliseconds, result.Text.Trim(), true));
    }

    // whisper.net consumes WAV files; capture pipelines hand us raw PCM.
    private static byte[] ToWav16kMono(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + pcm.Length).CopyTo(wav, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16000).CopyTo(wav, 24);
        BitConverter.GetBytes(32000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(pcm.Length).CopyTo(wav, 40);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _factory?.Dispose();
    }
}




