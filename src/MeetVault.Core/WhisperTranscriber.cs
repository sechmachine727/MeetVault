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
    // What the runtime actually loaded (valid after StartAsync). Lets the UI warn when a
    // requested GPU fell back to CPU, instead of silently showing the requested backend.
    public GpuBackend LoadedBackend => RuntimeOptions.LoadedLibrary switch
    {
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => GpuBackend.Cuda,
        RuntimeLibrary.Vulkan => GpuBackend.Vulkan,
        _ => GpuBackend.Cpu,
    };
    private WhisperFactory? _factory;
    private CancellationTokenSource? _cts;

    // The accelerator whisper's native actually initialized, read from its own log line
    // ("using <name> backend", or "no GPU found" -> CPU). LoadedBackend only says which native
    // DLL loaded, which stays a GPU name even when whisper then finds no usable GPU and
    // computes on the CPU; this reports the compute device.
    // The log is process-wide, so a reading is bound to this instance's own factory: it counts
    // when it arrived after that factory was created, which is when this transcriber's native
    // context began logging. Reusing the cached factory across meetings keeps reporting the
    // device it initialized, and an instance that never created a factory reports nothing, so a
    // second transcriber neither inherits nor clears the live one's reading.
    private static volatile string? _lastAccelerator;
    private static long _acceleratorSequence;
    private long _factorySequence;
    public string? AcceleratorInUse => _factory is not null && Volatile.Read(ref _acceleratorSequence) > _factorySequence ? _lastAccelerator : null;

private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ja", "vi" };

    // ggml/whisper report device selection and per-run timings through this logger
    // ("ggml_vulkan: Found 2 Vulkan devices", the chosen device, CUDA init errors). Routing
    // it into AppLog is what makes "is it really using the GPU" answerable from a log
    // instead of guesswork. Registered once per process; the subscription lives for the
    // process lifetime.
    private static readonly IDisposable? NativeLogSubscription = HookNativeLog();

    private static IDisposable? HookNativeLog()
    {
        try { return Whisper.net.Logger.LogProvider.AddLogger(OnNativeLog); }
        catch { return null; }
    }

    private static void OnNativeLog(Whisper.net.Logger.WhisperLogLevel level, string? message)
    {
        var text = message ?? "";
        var accel = ParseAccelerator(text);
        if (accel is not null) { _lastAccelerator = accel; Interlocked.Increment(ref _acceleratorSequence); }
        if (level == Whisper.net.Logger.WhisperLogLevel.Error) AppLog.Error("whisper-native: " + text);
        else if (level == Whisper.net.Logger.WhisperLogLevel.Warning) AppLog.Warn("whisper-native: " + text);
        else AppLog.Debug("whisper-native: " + text);
    }

    // whisper.cpp prints "whisper_backend_init_gpu: using <name> backend" when it offloads,
    // and "whisper_backend_init_gpu: no GPU found" when it does not. Null for every other line.
    internal static string? ParseAccelerator(string message)
    {
        const string prefix = "whisper_backend_init_gpu: ";
        var at = message.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = message[(at + prefix.Length)..].Trim();
        if (rest.StartsWith("no GPU found", StringComparison.Ordinal)) return "CPU";
        const string usingTag = "using ";
        const string backendTag = " backend";
        if (!rest.StartsWith(usingTag, StringComparison.Ordinal)) return null;
        var end = rest.IndexOf(backendTag, StringComparison.Ordinal);
        var name = end > usingTag.Length ? rest[usingTag.Length..end].Trim() : "";
        return name.Length > 0 ? name : null;
    }

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
        // The reading is bound to the factory, not the session: a second meeting reuses the
        // cached factory, so requiring a fresh per-session reading would drop the device it
        // already reported. Only the first creation snapshots the sequence.
        // Snapshot before creating the factory: a backend-init line the native emits while the
        // factory loads would otherwise be excluded and leave AcceleratorInUse null.
        if (_factory is null) { _factorySequence = Volatile.Read(ref _acceleratorSequence); _factory = CreateFactory(); }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        AppLog.Info("whisper: started model=" + Path.GetFileName(_modelPath) + " lang=" + _language + " loaded=" + LoadedBackend);
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
        AppLog.Info("whisper: requested backend=" + Backend + " model=" + Path.GetFileName(_modelPath) + " order=" + string.Join(",", RuntimeOptions.RuntimeLibraryOrder));
        if (Backend == GpuBackend.Cpu) return WhisperFactory.FromPath(_modelPath);
        try
        {
            var factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions { UseGpu = true, GpuDevice = GpuDevice });
            AppLog.Info("whisper: loaded=" + LoadedBackend);
            if (LoadedBackend != Backend) AppLog.Warn("whisper: requested " + Backend + " but the runtime loaded " + LoadedBackend);
            return factory;
        }
        catch (Exception ex)
        {
            AppLog.Error("whisper: GPU init failed for " + Backend + ", falling back to CPU", ex);
            return WhisperFactory.FromPath(_modelPath);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        IsRunning = false;
        AppLog.Info("whisper: stopped");
        return Task.CompletedTask;
    }

    public event Action<string, float>? LanguageDetected; // per-window detected language + probability (EN/JA/VI switching)

    // Transcribe one finalized 16 kHz mono PCM window; emits one Segment per whisper segment.
    public async Task TranscribeWindowAsync(byte[] pcm16kMono, long windowStartMs, string meetingId, CancellationToken ct = default)
    {
        if (_factory is null) throw new InvalidOperationException("Transcriber not started.");
        var startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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
        // Per-window cost on the loaded accelerator: the only way to tell a GPU that is
        // really working from one that idles between short bursts.
        AppLog.Debug("whisper: window " + (floats.Length / 16000.0).ToString("0.0") + "s on " + LoadedBackend
            + " -> " + (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds + "ms lang="
            + (lang ?? "?") + " " + prob.ToString("0.00") + " segments=" + index);
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







