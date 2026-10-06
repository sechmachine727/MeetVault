using NAudio.Wave;

namespace MeetVault.Core;

// Real mic capture behind IAudioCapture: 16 kHz mono WAV written chunked to disk.
// ponytail: mic only; per-app conference capture needs the WASAPI process-loopback native helper (spec 07.4).
public sealed class NAudioCapture : IAudioCapture, IDisposable
{
    public bool IsCapturing { get; private set; }
    public event Action<byte[]>? PcmReady; // 16-bit mono frames for the transcription pipeline

    private WaveInEvent? _mic;
    private WaveFileWriter? _writer;

    public string? MediaPath { get; private set; }

    public Task StartAsync(string micId, string appSource, CancellationToken ct)
    {
        if (IsCapturing) return Task.CompletedTask;
        var dir = Path.Combine(Path.GetTempPath(), "MeetVault", "media");
        Directory.CreateDirectory(dir);
        MediaPath = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav");
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        if (int.TryParse(micId, out var dev)) _mic.DeviceNumber = dev;
        _writer = new WaveFileWriter(MediaPath, _mic.WaveFormat);
        _mic.DataAvailable += (_, e) =>
        {
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            lock (_writer!) _writer.Write(chunk, 0, chunk.Length);
            PcmReady?.Invoke(chunk);
        };
        _mic.StartRecording();
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!IsCapturing) return Task.CompletedTask;
        _mic?.StopRecording();
        _mic?.Dispose(); _mic = null;
        lock (_writer!) { _writer.Dispose(); }
        _writer = null;
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _mic?.Dispose();
        _writer?.Dispose();
    }
}

