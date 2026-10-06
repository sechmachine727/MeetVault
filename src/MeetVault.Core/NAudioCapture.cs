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
        if (!int.TryParse(micId, out var dev)) throw new ArgumentException($"Mic id must be a device number, got '{micId}'. Available: {DescribeDevices()}.", nameof(micId));
        if (dev < 0 || dev >= WaveInEvent.DeviceCount) throw new ArgumentException($"Mic device {dev} out of range. Available: {DescribeDevices()}.", nameof(micId));
        if (IsCapturing) return Task.CompletedTask; // after validation: bad mic id errors even when capturing.
        var dir = Path.Combine(Path.GetTempPath(), "MeetVault", "media");
        Directory.CreateDirectory(dir);
        MediaPath = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav");
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _mic.DeviceNumber = dev;
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

private static string DescribeDevices()
    {
        if (WaveInEvent.DeviceCount == 0) return "none (no microphone found)";
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) names.Add($"{i}={WaveInEvent.GetCapabilities(i).ProductName}");
        return string.Join(", ", names);
    }

    // Real capture devices for the Settings picker. Empty when headless.
    public static IReadOnlyList<(int Id, string Name)> ListDevices()
    {
        var list = new List<(int, string)>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            string name;
            try { name = WaveInEvent.GetCapabilities(i).ProductName; }
            catch { name = "Microphone " + i; }
            list.Add((i, name));
        }
        return list;
    }

    public void Dispose()
    {
        _mic?.Dispose();
        _writer?.Dispose();
    }
}


