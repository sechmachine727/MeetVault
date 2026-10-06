using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace MeetVault.Core;

// Real transcription behind ISpeechTranscriber: whisper.cpp v1.9.4 via Whisper.net 1.9.1.
// ponytail: file-based finalized windows; streaming partials arrive with the VAD sliding-window upgrade.
public sealed class WhisperTranscriber : ISpeechTranscriber, IDisposable
{
    public event Action<Segment>? SegmentReady;
    public bool IsRunning { get; private set; }

    private readonly string _modelPath;
    private readonly string _language; // "auto" lets whisper detect EN/JA/VI per segment, including mid-conversation switches
    public bool UseGpu { get; }
    public GpuBackend Backend { get; }
    public int GpuDevice { get; }
    private WhisperFactory? _factory;
    private CancellationTokenSource? _cts;

private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ja", "vi" };

    public WhisperTranscriber(string modelPath, string language = "auto", GpuBackend backend = GpuBackend.Cpu, int gpuDevice = 0)
    {
        if (language is null) throw new ArgumentException("Language is required. Use auto, en, ja, vi, or a valid ISO code.", "language");
        if (!Known.Contains(language) && language.Length != 2) throw new ArgumentException("Unsupported language: " + language + ". Use auto, en, ja, vi, or a valid ISO code.", "language");
        _modelPath = modelPath;
        _language = language;
        Backend = backend;
        GpuDevice = gpuDevice;
        UseGpu = backend != GpuBackend.Cpu;
    }

    public static async Task DownloadModelAsync(string destPath, GgmlType type, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, QuantizationType.NoQuantization, ct);
        var tmp = destPath + ".tmp"; // atomic: interrupted download never passes the File.Exists gate
        using var fileWriter = File.OpenWrite(tmp);
        await modelStream.CopyToAsync(fileWriter, ct);
        File.Move(tmp, destPath, overwrite: true);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!File.Exists(_modelPath)) throw new FileNotFoundException($"Whisper model not found: {_modelPath}. Download the multilingual base model to models/ggml-base.bin.", _modelPath);
        _factory ??= CreateFactory();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        return Task.CompletedTask;
    }

    // Picks the concrete runtime. The order is set on every call (not just for Vulkan):
    // RuntimeLibraryOrder is process-static, so leaving it at a previous GPU choice would
    // keep loading that backend after the user switches back to CPU. A GPU-init failure
    // falls back to CPU rather than failing the meeting.
    private WhisperFactory CreateFactory()
    {
        RuntimeOptions.RuntimeLibraryOrder = Backend switch
        {
            GpuBackend.Vulkan => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            GpuBackend.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        };
        if (Backend == GpuBackend.Cpu) return WhisperFactory.FromPath(_modelPath);
        try
        {
            return WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions { UseGpu = true, GpuDevice = GpuDevice });
        }
        catch
        {
            return WhisperFactory.FromPath(_modelPath);
        }
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
        // Link the caller's token with Stop's CTS so a paused/stopped session cancels
        // an in-flight window instead of finishing it after the gap.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? default);
        using var processor = _factory.CreateBuilder().WithLanguage(_language).Build();
        var floats = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < floats.Length; i++) floats[i] = BitConverter.ToInt16(pcm16kMono, 2 * i) / 32768f;
        var (lang, prob) = processor.DetectLanguageWithProbability(floats, ["en", "ja", "vi"]);
        LanguageDetected?.Invoke(lang ?? "unknown", prob);
        using var stream = new MemoryStream(ToWav16kMono(pcm16kMono));
        var index = 0;
        await foreach (var result in processor.ProcessAsync(stream, linked.Token))
        {
            var text = result.Text.Trim();
            if (IsNonSpeech(text)) continue; // silence tokens must not become transcript lines
            SegmentReady?.Invoke(new Segment($"{meetingId}-w{windowStartMs}-{index++}", "", windowStartMs + (long)result.Start.TotalMilliseconds, windowStartMs + (long)result.End.TotalMilliseconds, text, true));
        }
    }

    // whisper.cpp emits bracketed silence markers on quiet windows; they are not speech.
    private static bool IsNonSpeech(string text)
    {
        if (text.Length == 0) return true;
        var normalized = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "blankaudio" or "silence" or "inaudible";
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







