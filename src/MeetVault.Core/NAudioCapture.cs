using NAudio.Wave;

namespace MeetVault.Core;

// Real capture behind IAudioCapture. Source is the mic (WaveInEvent) or the system
// render loopback (WasapiLoopbackCapture), which is how a conference app's audio is
// captured. Loopback is resampled to 16 kHz mono with NAudio's resampler so both
// sources feed the same 16 kHz mono WAV + transcription pipeline.
// ponytail: system-wide loopback, not per-process; per-app needs the WASAPI process-loopback helper.
public sealed class NAudioCapture : IAudioCapture, IDisposable
{
    public bool IsCapturing { get; private set; }
    public event Action<byte[]>? PcmReady; // 16-bit mono frames for the transcription pipeline

    private WaveInEvent? _mic;
    private WasapiLoopbackCapture? _loop;
    private BufferedWaveProvider? _buffer;
    private MediaFoundationResampler? _resampler;
    private Thread? _pump;
    private volatile bool _running;
    private WavSink? _sink;
    private string? _reusePath;

    public string? MediaPath => _sink?.Path ?? _reusePath;

    // Continue an existing meeting WAV (crash recovery) instead of starting a new one.
    public void ReuseFile(string path) { if (_sink is null && !IsCapturing) _reusePath = path; }

    public Task StartAsync(string micId, string appSource, CancellationToken ct)
    {
        if (IsCapturing) return Task.CompletedTask;
        _sink ??= new WavSink(_reusePath ?? NewMediaPath());
        _reusePath = null;
        IsCapturing = true;

        if (string.Equals(appSource, "system", StringComparison.OrdinalIgnoreCase)) StartSystem();
        else StartMic(micId);
        return Task.CompletedTask;
    }

    private void StartMic(string micId)
    {
        if (!int.TryParse(micId, out var dev)) { IsCapturing = false; throw new ArgumentException($"Mic id must be a device number, got '{micId}'. Available: {DescribeDevices()}.", nameof(micId)); }
        if (dev < 0 || dev >= WaveInEvent.DeviceCount) { IsCapturing = false; throw new ArgumentException($"Mic device {dev} out of range. Available: {DescribeDevices()}.", nameof(micId)); }
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), DeviceNumber = dev };
        _mic.DataAvailable += (_, e) =>
        {
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            WriteChunk(chunk);
        };
        _mic.StartRecording();
        AppLog.Info("capture: mic device=" + dev + " '" + SafeName(dev) + "' file=" + MediaPath);
    }

    // Loopback captures whatever the speakers play - a Teams/Zoom call, a video, etc.
    private void StartSystem()
    {
        _loop = new WasapiLoopbackCapture();
        _buffer = new BufferedWaveProvider(_loop.WaveFormat) { DiscardOnBufferOverflow = true };
        _loop.DataAvailable += (_, e) => { try { _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch (Exception ex) { AppLog.Warn("capture: loopback buffer :: " + ex.Message); } };
        _loop.RecordingStopped += (_, e) => { if (e.Exception is not null) AppLog.Error("capture: loopback stopped unexpectedly", e.Exception); };
        // NAudio resampler handles the device format (often 48 kHz stereo float) -> 16 kHz mono.
        _resampler = new MediaFoundationResampler(_buffer, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        _running = true;
        _pump = new Thread(PumpLoopback) { IsBackground = true, Name = "MeetVaultLoopback" };
        _pump.Start();
        _loop.StartRecording();
        AppLog.Info("capture: system loopback device='" + (_loop.WaveFormat) + "' file=" + MediaPath);
    }

    private void PumpLoopback()
    {
        var buffer = new byte[3200]; // 100 ms of 16 kHz mono 16-bit
        while (_running)
        {
            try
            {
                var read = _resampler!.Read(buffer, 0, buffer.Length);
                if (read <= 0) { Thread.Sleep(20); continue; }
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                WriteChunk(chunk);
            }
            catch (Exception ex) { AppLog.Warn("capture: loopback pump :: " + ex.Message); Thread.Sleep(50); }
        }
    }

    private void WriteChunk(byte[] chunk)
    {
        lock (_sink!) _sink.Write(chunk);
        PcmReady?.Invoke(chunk);
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!IsCapturing) return Task.CompletedTask;
        IsCapturing = false;
        _running = false;
        if (_mic is not null) { try { _mic.StopRecording(); } catch { } _mic.Dispose(); _mic = null; }
        if (_loop is not null) { try { _loop.StopRecording(); } catch { } _loop.Dispose(); _loop = null; }
        _pump?.Join(1000); _pump = null;
        _resampler?.Dispose(); _resampler = null;
        _buffer = null;
        lock (_sink!) _sink.FinalizeHeader();
        AppLog.Info("capture: stopped file=" + MediaPath);
        return Task.CompletedTask;
    }

    private static string NewMediaPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault", "media");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("n").Substring(0, 6) + ".wav");
    }

    private static string SafeName(int dev)
    {
        try { return WaveInEvent.GetCapabilities(dev).ProductName; } catch { return "device " + dev; }
    }

    private static string DescribeDevices()
    {
        if (WaveInEvent.DeviceCount == 0) return "none (no microphone found)";
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) names.Add($"{i}={WaveInEvent.GetCapabilities(i).ProductName}");
        return string.Join(", ", names);
    }

    // Capture devices for the Settings picker. Empty when headless.
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
        if (IsCapturing) { try { StopAsync(CancellationToken.None).Wait(1500); } catch { } }
        _mic?.Dispose();
        _loop?.Dispose();
        _resampler?.Dispose();
        if (_sink is null) return;
        lock (_sink) { _sink.Dispose(); }
        _sink = null;
    }

    // Append-safe 16 kHz mono WAV writer: the header's size fields are rewritten on
    // FinalizeHeader (after each stop), so the file is playable without closing it.
    internal sealed class WavSink : IDisposable
    {
        private readonly FileStream _fs;
        private long _dataBytes;
        public string Path { get; }

        public WavSink(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _dataBytes = Math.Max(0, _fs.Length - 44);
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

