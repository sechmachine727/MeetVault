using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetVault.Core;

// Real capture behind IAudioCapture. It always records the microphone and the system
// audio together, mixed to 16 kHz mono, so the user's own voice is never dropped when
// they pick "system audio". Whatever the speakers play (a Teams/Zoom call, a video) is
// captured as well.
//
// StartAsync hints:
//   micId     device index, or "off" to skip the microphone.
//   appSource "off" to skip system audio, "all" for every app, or a process id to
//             capture one application (and its child processes) through process loopback.
//
// ponytail: mic is single device; per-process capture falls back to system-wide when the
// process-loopback API is unavailable (Windows < 10 20348 or a failed activation).
public sealed class NAudioCapture : IAudioCapture, IDisposable
{
    public bool IsCapturing { get; private set; }
    public event Action<byte[]>? PcmReady; // 16-bit mono 16 kHz frames

    private WaveInEvent? _mic;
    private IWaveIn? _system;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _sysBuffer;
    private ISampleProvider? _chain;
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

        var inputs = new List<ISampleProvider>();
        var micOn = !string.IsNullOrWhiteSpace(micId) && !string.Equals(micId, "off", StringComparison.OrdinalIgnoreCase);
        var sysOn = !string.IsNullOrWhiteSpace(appSource) && !string.Equals(appSource, "off", StringComparison.OrdinalIgnoreCase);

        // A bad mic id is a caller error and must surface; a transient mic failure is logged
        // so a working system-audio source can still record.
        if (micOn) { try { inputs.Add(StartMic(micId)); } catch (ArgumentException) { throw; } catch (Exception ex) { AppLog.Error("capture: microphone failed (" + micId + ")", ex); } }
        if (sysOn) { try { inputs.Add(StartSystem(appSource)); } catch (Exception ex) { AppLog.Error("capture: system audio failed (" + appSource + ")", ex); } }

        if (inputs.Count == 0)
        {
            try { _mic?.Dispose(); } catch { } _mic = null;
            try { _system?.Dispose(); } catch { } _system = null;
            throw new InvalidOperationException("No audio source could be started (microphone and system audio both unavailable).");
        }

        _sink ??= new WavSink(_reusePath ?? NewMediaPath());
        _reusePath = null;
        // ReadFully keeps the mix continuous: it pads silence for a starved source instead
        // of returning 0, so the paced pump always emits a real-time stream.
        _chain = inputs.Count == 1 ? inputs[0] : new MixingSampleProvider(inputs) { ReadFully = true };
        AppLog.Info("capture: chain format=" + _chain.WaveFormat);
        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "MeetVaultMix" };
        _pump.Start();
        IsCapturing = true;
        AppLog.Info("capture: started mic=" + (micOn ? "on" : "off") + " system=" + (sysOn ? appSource : "off") + " sources=" + inputs.Count + " file=" + MediaPath);
        return Task.CompletedTask;
    }

    private ISampleProvider StartMic(string micId)
    {
        if (!int.TryParse(micId, out var dev)) throw new ArgumentException($"Mic id must be a device number, got '{micId}'. Available: {DescribeDevices()}.", nameof(micId));
        if (dev < 0 || dev >= WaveInEvent.DeviceCount) throw new ArgumentException($"Mic device {dev} out of range. Available: {DescribeDevices()}.", nameof(micId));
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), DeviceNumber = dev };
        _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true };
        _mic.DataAvailable += (_, e) => { try { _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
        _mic.StartRecording();
        AppLog.Info("capture: mic device=" + dev + " '" + SafeName(dev) + "'");
        return _micBuffer.ToSampleProvider(); // already 16 kHz mono
    }

    private ISampleProvider StartSystem(string appSource)
    {
        // A positive integer appSource means "capture this process tree" (one app).
        var capture = int.TryParse(appSource, out var pid) && pid > 0
            ? (IWaveIn)new ProcessLoopbackCapture((uint)pid)
            : new WasapiLoopbackCapture();
        WireSystem(capture);
        try
        {
            capture.StartRecording();
        }
        catch (Exception ex) when (capture is ProcessLoopbackCapture)
        {
            // Per-app capture is unavailable here (process loopback needs Windows 10
            // 20348+ and is often unsupported in a VM). Fall back to system-wide loopback.
            AppLog.Warn("capture: process loopback pid=" + pid + " failed, using system-wide :: " + ex.Message);
            try { capture.Dispose(); } catch { }
            capture = new WasapiLoopbackCapture();
            WireSystem(capture);
            capture.StartRecording();
        }
        _system = capture;
        AppLog.Info("capture: system audio (" + capture.GetType().Name + ") format=" + capture.WaveFormat);
        return ToMono16k(_sysBuffer!.ToSampleProvider());
    }

    private void WireSystem(IWaveIn capture)
    {
        _sysBuffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
        capture.DataAvailable += (_, e) => { try { _sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
    }

    // System audio arrives as float often at 48 kHz stereo; make it 16 kHz mono.
    private static ISampleProvider ToMono16k(ISampleProvider provider)
    {
        var mono = provider.WaveFormat.Channels > 1 ? provider.ToMono() : provider;
        return mono.WaveFormat.SampleRate == 16000 ? mono : new WdlResamplingSampleProvider(mono, 16000);
    }

    // Reads the mixed 16 kHz stream and writes it paced to real time. Without the pace,
    // WdlResamplingSampleProvider pads silence when its input is starved, so the loop
    // would free-run and write ~100x too much data (and time-shift the transcript).
    private void Pump()
    {
        const int samplesPerRead = 320; // 20 ms at 16 kHz
        var floats = new float[samplesPerRead];
        var bytes = new byte[samplesPerRead * 2];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        var sawInput = false;
        while (_running)
        {
            int read;
            try { read = _chain!.Read(floats, 0, floats.Length); }
            catch (Exception ex) { AppLog.Warn("capture: mix read :: " + ex.GetType().Name + ": " + ex.Message); Thread.Sleep(50); continue; }
            if (!sawInput && ((_micBuffer?.BufferedBytes ?? 0) > 0 || (_sysBuffer?.BufferedBytes ?? 0) > 0)) sawInput = true;
            if (read <= 0) { Thread.Sleep(5); continue; }
            for (var i = 0; i < read; i++)
            {
                var v = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)(v & 0xFF);
                bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
            }
            var chunk = new byte[read * 2];
            Buffer.BlockCopy(bytes, 0, chunk, 0, chunk.Length);
            WriteChunk(chunk);
            produced += read;
            // Keep ~16 kHz in real time: sleep off any lead over the wall clock.
            var ahead = produced * 1000.0 / 16000.0 - clock.Elapsed.TotalMilliseconds;
            if (ahead > 1) Thread.Sleep((int)Math.Min(ahead, 40));
        }
        // No input the whole run means the devices opened but delivered no samples. The
        // transcript will be empty; the reason is usually a wrong/disconnected device.
        if (!sawInput) AppLog.Warn("capture: no audio samples were captured (produced=" + produced + " output samples, all silence)");
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
        try { _mic?.StopRecording(); } catch { } _mic?.Dispose(); _mic = null;
        try { _system?.StopRecording(); } catch { } _system?.Dispose(); _system = null;
        _pump?.Join(1000); _pump = null;
        _micBuffer = null; _sysBuffer = null; _chain = null;
        if (_sink is not null) { lock (_sink) _sink.FinalizeHeader(); }
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

    // Applications currently rendering audio, for the "capture one app" picker.
    public static IReadOnlyList<(uint Pid, string Name)> ListAudioApps()
    {
        var list = new List<(uint, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var seen = new HashSet<uint>();
            for (var i = 0; i < sessions.Count; i++)
            {
                uint pid;
                try { pid = sessions[i].GetProcessID; } catch { continue; }
                if (pid == 0 || !seen.Add(pid)) continue;
                string name;
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.MainModule?.FileVersionInfo?.FileDescription is { Length: > 0 } d ? d : p.ProcessName; }
                catch { name = "process " + pid; }
                list.Add((pid, name.Trim()));
            }
        }
        catch (Exception ex) { AppLog.Warn("capture: could not list audio apps :: " + ex.Message); }
        return list;
    }

    public void Dispose()
    {
        if (IsCapturing) { try { StopAsync(CancellationToken.None).Wait(1500); } catch { } }
        _mic?.Dispose();
        _system?.Dispose();
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
