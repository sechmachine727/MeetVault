using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetVault.Core;

// Real capture behind IAudioCapture. It always records the microphone and the system
// audio together, mixed at the archive rate (mono), so the user's own voice is never
// dropped when they pick "system audio" and nothing above 8 kHz is thrown away. A 16 kHz
// mono copy of the same mix is handed to the transcriber and the diarizer.
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
    // 48 kHz keeps the whole audible band; the old 16 kHz mix is what made recordings sound
    // muffled and telephone-like. The recognizer still requires 16 kHz mono.
    private const int ArchiveRate = 48000;
    private const int InferenceRate = 16000;
    private const int ArchiveBitRate = 256000; // AAC, as asked for

    // Written by the pump thread on failure and read on the UI thread, so it needs a barrier.
    private volatile bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; private set => _isCapturing = value; }
    public event Action<byte[]>? PcmReady; // 16-bit mono 16 kHz frames
    // Raised when the pump dies on its own (disk error, sink lost). The session would
    // otherwise keep reporting "Recording" and later save a truncated meeting as complete.
    public event Action<string>? CaptureFailed;

    private WaveInEvent? _mic;
    private IWaveIn? _system;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _sysBuffer;
    private ISampleProvider? _chain;
    private InferenceDownmixer? _infer;
    private Thread? _pump;
    private volatile bool _running;
    private WavSink? _sink;
    private string? _reusePath;  // set by ReuseFile: crash recovery continues this file
    private string? _resumePath; // set by StopAsync: pause/resume continues this file
    private string? _finalPath;  // set by FinishAsync: the compacted recording to keep

    public string? MediaPath => _finalPath ?? _sink?.Path ?? _resumePath ?? _reusePath;

    // Continue an existing meeting file (crash recovery) instead of starting a new one.
    public void ReuseFile(string path) { if (_sink is null && !IsCapturing) _reusePath = path; }

    // Drop a reuse request that never became a recovery, so the next meeting does not append
    // to the crashed meeting's file (two meetings on one recording, then that file deleted).
    // Both are cleared: a recovery that failed after capture opened leaves the crashed file in
    // _resumePath, and leaving it there would record the next meeting into the crashed one.
    public void ClearReuse() { _reusePath = null; _resumePath = null; }

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

        _finalPath = null;
        _sink ??= new WavSink(_reusePath ?? _resumePath ?? NewMediaPath(), ArchiveRate);
        _reusePath = null;
        // ReadFully keeps the stream continuous: it pads silence for a starved source instead
        // of returning 0, so the paced pump always emits a real-time stream. The mixer wraps
        // a single input too, otherwise a lone microphone that goes quiet would return 0 and
        // the recording, and the transcript timeline, would fall behind the wall clock.
        _chain = new MixingSampleProvider(inputs) { ReadFully = true };
        _infer = new InferenceDownmixer();
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
        // Ask for the archive rate first; a device that refuses 48 kHz falls back to 16 kHz,
        // which is band-limited but still recorded rather than losing the microphone.
        if (!TryStartMic(dev, ArchiveRate) && !TryStartMic(dev, InferenceRate))
            throw new InvalidOperationException("Microphone device " + dev + " would not open at " + ArchiveRate + " or " + InferenceRate + " Hz.");
        AppLog.Info("capture: mic device=" + dev + " '" + SafeName(dev) + "' rate=" + _mic!.WaveFormat.SampleRate);
        return ToMonoRate(_micBuffer!.ToSampleProvider(), ArchiveRate); // mic is always mono
    }

    private bool TryStartMic(int dev, int rate)
    {
        try
        {
            _mic = new WaveInEvent { WaveFormat = new WaveFormat(rate, 16, 1), DeviceNumber = dev };
            _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true };
            _mic.DataAvailable += (_, e) => { try { _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
            _mic.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("capture: mic device=" + dev + " would not open at " + rate + " Hz :: " + ex.Message);
            try { _mic?.Dispose(); } catch { } _mic = null;
            _micBuffer = null;
            return false;
        }
    }

    private ISampleProvider StartSystem(string appSource)
    {
        // A positive integer appSource means "capture this process tree" (one app).
        var capture = int.TryParse(appSource, out var pid) && pid > 0
            ? (IWaveIn)new ProcessLoopbackCapture((uint)pid)
            : new WasapiLoopbackCapture();
        try
        {
            StartSystemCapture(capture);
        }
        catch (Exception ex) when (capture is ProcessLoopbackCapture)
        {
            // Per-app capture is unavailable here (process loopback needs Windows 10
            // 20348+ and is often unsupported in a VM). Fall back to system-wide loopback.
            AppLog.Warn("capture: process loopback pid=" + pid + " failed, using system-wide :: " + ex.Message);
            capture = new WasapiLoopbackCapture();
            StartSystemCapture(capture);
        }
        _system = capture;
        AppLog.Info("capture: system audio (" + capture.GetType().Name + ") format=" + capture.WaveFormat);
        return ToMonoRate(_sysBuffer!.ToSampleProvider(), ArchiveRate);
    }

    // The buffer is built from the format the device reports *after* it is open. Process
    // loopback only learns its real mix format during StartRecording, so building the buffer
    // first would mis-read 44.1 kHz or non-stereo output as the 48 kHz stereo default.
    private void StartSystemCapture(IWaveIn capture)
    {
        try
        {
            capture.StartRecording();
            WireSystem(capture);
        }
        catch
        {
            // A capture that fails to open must not leak its device; the caller's fallback
            // creates a replacement, and the outer StartAsync logs the failure.
            try { capture.Dispose(); } catch { }
            throw;
        }
    }

    private void WireSystem(IWaveIn capture)
    {
        _sysBuffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
        capture.DataAvailable += (_, e) => { try { _sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
    }

    // System audio arrives as float, often 48 kHz stereo; the archive is mono. The resampler
    // is only built when the rate actually differs.
    private static ISampleProvider ToMonoRate(ISampleProvider provider, int rate)
    {
        var mono = provider.WaveFormat.Channels > 1 ? provider.ToMono() : provider;
        return mono.WaveFormat.SampleRate == rate ? mono : new WdlResamplingSampleProvider(mono, rate);
    }

    // Reads the mixed archive-rate stream, writes the full-band audio to disk and paces the
    // loop to real time. Without the pace, WdlResamplingSampleProvider pads silence when its
    // input is starved, so the loop would free-run and write ~100x too much data (and
    // time-shift the transcript).
    private void Pump()
    {
        const int samplesPerRead = 480; // 10 ms at 48 kHz
        var floats = new float[samplesPerRead];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        var sawInput = false;
        while (_running)
        {
            var sink = _sink;
            if (sink is null) break;
            try
            {
                int read;
                try { read = _chain!.Read(floats, 0, floats.Length); }
                catch (Exception ex) { AppLog.Warn("capture: mix read :: " + ex.GetType().Name + ": " + ex.Message); Thread.Sleep(50); continue; }
                if (!sawInput && ((_micBuffer?.BufferedBytes ?? 0) > 0 || (_sysBuffer?.BufferedBytes ?? 0) > 0)) sawInput = true;
                if (read <= 0) { Thread.Sleep(5); continue; }
                // Re-check under the lock: a join on stop can time out while this thread is
                // inside the PcmReady callback, after which the sink is disposed.
                lock (sink) { if (!ReferenceEquals(_sink, sink)) break; sink.Write(ToPcm16(floats, read)); }
                produced += read;
                var pcm16k = _infer?.Push(floats, read);
                if (pcm16k is { Length: > 0 }) PcmReady?.Invoke(pcm16k);
                // Keep the archive rate in real time: sleep off any lead over the wall clock.
                var ahead = produced * 1000.0 / ArchiveRate - clock.Elapsed.TotalMilliseconds;
                if (ahead > 1) Thread.Sleep((int)Math.Min(ahead, 40));
            }
            // This is a raw Thread: App.UnhandledException only covers the UI thread, so an
            // exception escaping here terminates the process and takes the recording with it.
            // A disk error, or a sink disposed during shutdown, must stop the pump instead.
            catch (Exception ex)
            {
                IsCapturing = false;
                _running = false;
                AppLog.Error("capture: pump stopped :: " + ex.GetType().Name + ": " + ex.Message);
                // A throwing subscriber must not escape onto this thread; that is the very
                // failure mode this catch exists to prevent.
                try { CaptureFailed?.Invoke(ex.GetType().Name + ": " + ex.Message); }
                catch (Exception inner) { AppLog.Warn("capture: failure handler threw :: " + inner.Message); }
                break;
            }
        }
        // No input the whole run means the devices opened but delivered no samples. The
        // transcript will be empty; the reason is usually a wrong/disconnected device.
        if (!sawInput) AppLog.Warn("capture: no audio samples were captured (produced=" + produced + " output samples, all silence)");
    }

    private static byte[] ToPcm16(float[] samples, int count)
    {
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var v = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)(v & 0xFF);
            bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    // Pauses capture and releases the file so it can be read back. The recording stays
    // resumable: the next StartAsync reopens this exact file and appends.
    public Task StopAsync(CancellationToken ct)
    {
        if (StopCore()) { _resumePath = _lastPath; }
        AppLog.Info("capture: paused file=" + MediaPath);
        return Task.CompletedTask;
    }

    private string? _lastPath;

    // Stops the devices and the pump and leaves the working file finalized but unlinked, so
    // the caller decides whether it is a pause (reopen it later) or a finish (compact it).
    private bool StopCore()
    {
        // A dead pump clears IsCapturing without tearing the devices or the sink down, so the
        // guard tests what still needs releasing rather than only the flag. Without this, a
        // pause after a pump failure would leak the capture devices and never finalize the WAV.
        if (!IsCapturing && _mic is null && _system is null && _sink is null) return false;
        IsCapturing = false;
        _running = false;
        try { _mic?.StopRecording(); } catch { } _mic?.Dispose(); _mic = null;
        try { _system?.StopRecording(); } catch { } _system?.Dispose(); _system = null;
        _pump?.Join(1000); _pump = null;
        _micBuffer = null; _sysBuffer = null; _chain = null; _infer = null;
        _lastPath = _sink?.Path;
        ReleaseSink();
        return true;
    }

    // The meeting is over: stop, release the working file and compact it. StopAsync only
    // pauses, so nothing else owns the "this recording is final" decision.
    public async Task FinishAsync(CancellationToken ct)
    {
        StopCore();
        var working = _lastPath ?? _resumePath ?? _reusePath;
        ReleaseSink();
        _reusePath = null;
        _resumePath = null;
        _lastPath = null;
        // Encode off the caller's thread: this reads and re-encodes the whole recording, and
        // the caller of "Stop and save" is the UI thread.
        _finalPath = working is null ? null : await Task.Run(() => Compact(working), ct);
        AppLog.Info("capture: finished file=" + MediaPath);
    }

    private void ReleaseSink()
    {
        if (_sink is null) return;
        lock (_sink)
        {
            try { _sink.FinalizeHeader(); } catch { }
            try { _sink.Dispose(); } catch { }
        }
        _sink = null;
    }

    // Finalizes the working WAV into a compact AAC (.m4a) file. The WAV is the live format
    // because pause/resume and crash recovery both need a file that can be reopened and
    // appended; the meeting only needs a small file once it is over.
    // The WAV is deliberately left on disk: the caller deletes it only after the new path is
    // persisted, so a crash or a failed save can never leave a row pointing at a deleted file.
    // ponytail: encode on finish, not live; a live encoder would need a streaming MP4 muxer.
    // Returns the path to keep: the .m4a on success, the WAV when encoding is unavailable.
    public static string? Compact(string? wavPath)
    {
        if (wavPath is null || !File.Exists(wavPath)) return wavPath;
        var m4a = Path.ChangeExtension(wavPath, ".m4a");
        try
        {
            using (var reader = new WaveFileReader(wavPath))
                MediaFoundationEncoder.EncodeToAac(reader, m4a, ArchiveBitRate);
            var m4aBytes = new FileInfo(m4a).Length;
            if (m4aBytes == 0)
            {
                AppLog.Warn("capture: AAC encoder produced an empty file; keeping the WAV");
                TryDeleteFile(m4a);
                return wavPath;
            }
            AppLog.Info("capture: encoded " + Path.GetFileName(m4a) + " " + m4aBytes / 1024 + " KB from " + new FileInfo(wavPath).Length / 1024 + " KB WAV");
            return m4a;
        }
        catch (Exception ex)
        {
            AppLog.Warn("capture: AAC encode failed (" + ex.GetType().Name + ": " + ex.Message + "); keeping the WAV");
            TryDeleteFile(m4a);
            return wavPath;
        }
    }

    // MIME for a finalized recording, so playback gets the right type.
    public static string ArchiveMime(string path) =>
        path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "audio/mp4" : "audio/wav";

    public static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

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
        ReleaseSink();
    }

    // Append-safe WAV writer at the archive rate: the header's size fields are rewritten on
    // The archive mix is 48 kHz mono; the transcriber and the diarizer both require 16 kHz
    // mono. One resampler off the same stream converts it without capturing the sources
    // twice. Encapsulated so the conversion can be exercised without audio hardware: push
    // 48 kHz samples in, read back a third as many 16 kHz samples.
    internal sealed class InferenceDownmixer
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly ISampleProvider _resampler;
        private float[] _scratch = new float[2048];

        public InferenceDownmixer()
        {
            _buffer = new BufferedWaveProvider(new WaveFormat(ArchiveRate, 16, 1))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            _resampler = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), InferenceRate);
        }

        // Returns the 16 kHz mono PCM for this slice of the archive mix.
        public byte[] Push(float[] archiveSamples, int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            _buffer.AddSamples(ToPcm16(archiveSamples, count), 0, count * 2);
            var want = count / (ArchiveRate / InferenceRate);
            if (want <= 0) return Array.Empty<byte>();
            if (_scratch.Length < want) _scratch = new float[want];
            var got = 0;
            while (got < want)
            {
                var read = _resampler.Read(_scratch, got, want - got);
                if (read <= 0) break;
                got += read;
            }
            return got > 0 ? ToPcm16(_scratch, got) : Array.Empty<byte>();
        }
    }

    // Append-safe WAV writer at the archive rate: the header's size fields are rewritten on
    // FinalizeHeader (after each stop), so the file is playable without closing it.
    internal sealed class WavSink : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _sampleRate;
        private long _dataBytes;
        public string Path { get; }

        public WavSink(string path, int sampleRate)
        {
            _sampleRate = sampleRate;
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
            BitConverter.GetBytes(_sampleRate).CopyTo(h, 24);
            BitConverter.GetBytes(_sampleRate * 2).CopyTo(h, 28);
            BitConverter.GetBytes((short)2).CopyTo(h, 32);
            BitConverter.GetBytes((short)16).CopyTo(h, 34);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
            _fs.Write(h, 0, h.Length);
        }
    }
}
