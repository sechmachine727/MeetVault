using NAudio.Wave;

namespace MeetVault.Core;

// Real mic capture behind IAudioCapture: 16 kHz mono WAV written chunked to disk.
// ponytail: mic only; per-app conference capture needs the WASAPI process-loopback native helper (spec 07.4).
public sealed class NAudioCapture : IAudioCapture, IDisposable
{
    public bool IsCapturing { get; private set; }
    public event Action<byte[]>? PcmReady; // 16-bit mono frames for the transcription pipeline

    private WaveInEvent? _mic;
    private WavSink? _sink;
    private string? _reusePath;

    public string? MediaPath => _sink?.Path ?? _reusePath;

    // Continue an existing meeting WAV (crash recovery) instead of starting a new one.
    public void ReuseFile(string path) { if (_sink is null && !IsCapturing) _reusePath = path; }

    public Task StartAsync(string micId, string appSource, CancellationToken ct)
    {
        if (!int.TryParse(micId, out var dev)) throw new ArgumentException($"Mic id must be a device number, got '{micId}'. Available: {DescribeDevices()}.", nameof(micId));
        if (dev < 0 || dev >= WaveInEvent.DeviceCount) throw new ArgumentException($"Mic device {dev} out of range. Available: {DescribeDevices()}.", nameof(micId));
        if (IsCapturing) return Task.CompletedTask; // after validation: bad mic id errors even when capturing.
        // One WAV per meeting, kept open across pause/resume so the audio is never
        // split. Moved out of %TEMP% so a cleanup cannot delete a saved recording.
        _sink ??= new WavSink(_reusePath ?? NewMediaPath());
        _reusePath = null;
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _mic.DeviceNumber = dev;
        _mic.DataAvailable += (_, e) =>
        {
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            lock (_sink!) _sink.Write(chunk);
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
        // Finalize the WAV header now so the file is playable before the page unloads.
        lock (_sink!) _sink.FinalizeHeader();
        IsCapturing = false;
        return Task.CompletedTask;
    }

    private static string NewMediaPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault", "media");
        Directory.CreateDirectory(dir);
        // Timestamp + short guid: second-resolution names could otherwise collide.
        return Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("n").Substring(0, 6) + ".wav");
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
        if (_sink is null) return;
        lock (_sink) { _sink.Dispose(); }
        _sink = null;
    }

    // Append-safe 16 kHz mono WAV writer: the header's size fields are rewritten on
    // FinalizeHeader (after each stop), so the file is playable without closing it.
    private sealed class WavSink : IDisposable
    {
        private readonly FileStream _fs;
        private long _dataBytes;
        public string Path { get; }

        public WavSink(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _dataBytes = Math.Max(0, _fs.Length - 44); // reuse: continue an existing file
            _fs.SetLength(Math.Max(44, _fs.Length));
            _fs.Seek(0, SeekOrigin.Begin);
            WriteHeader();
            _fs.Seek(_fs.Length, SeekOrigin.Begin);
            FinalizeHeader();
        }

        public void Write(byte[] data)
        {
            _fs.Write(data, 0, data.Length);
            _dataBytes += data.Length;
        }

        public void FinalizeHeader()
        {
            var end = _fs.Position;
            _fs.Seek(4, SeekOrigin.Begin); _fs.Write(BitConverter.GetBytes((int)(36 + _dataBytes)));
            _fs.Seek(40, SeekOrigin.Begin); _fs.Write(BitConverter.GetBytes((int)_dataBytes));
            _fs.Seek(end, SeekOrigin.Begin);
            _fs.Flush();
        }

        public void Dispose()
        {
            try { FinalizeHeader(); } catch { }
            _fs.Dispose();
        }

        private void WriteHeader()
        {
            var h = new byte[44];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
            System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
            BitConverter.GetBytes(16).CopyTo(h, 16);
            BitConverter.GetBytes((short)1).CopyTo(h, 20);
            BitConverter.GetBytes((short)1).CopyTo(h, 22);
            BitConverter.GetBytes(16000).CopyTo(h, 24);
            BitConverter.GetBytes(32000).CopyTo(h, 28);
            BitConverter.GetBytes((short)2).CopyTo(h, 32);
            BitConverter.GetBytes((short)16).CopyTo(h, 34);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
            _fs.Write(h, 0, h.Length);
        }
    }
}

