using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetVault.Core;

// Captures one process (and its child processes) through the WASAPI process-loopback
// API: ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
// available on Windows 10 build 20348 and later. This is WasapiLoopbackCapture scoped
// to a single application. NAudio does not expose this, so the interop is hand-written.
//
// Verification: the code path is exercised when a specific app is chosen in Settings.
// It cannot be proven on a headless or GPU-P VM; NAudioCapture falls back to system-wide
// loopback when construction or activation fails, and the failure is logged.
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualProcessLoopback = "VAD\\Process_Loopback";
    private const int AudclntStreamFlagsLoopback = 0x00020000;
    private const int AudclntStreamFlagsEventCallback = 0x00040000;
    private const int AudclntShareModeShared = 0;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeIncludeTree = 0;

    private readonly uint _pid;
    private readonly Guid _iidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private readonly Guid _iidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private AutoResetEvent? _signal;
    private Thread? _thread;
    private volatile bool _running;
    private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public ProcessLoopbackCapture(uint pid) { _pid = pid; }

    public WaveFormat WaveFormat { get => _format; set => _format = value; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        var handler = new ActivationHandler();
        var activation = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams { TargetProcessId = _pid, ProcessLoopbackMode = ProcessLoopbackModeIncludeTree },
        };
        var size = Marshal.SizeOf<AudioClientActivationParams>();
        var blobPtr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(activation, blobPtr, false);
        var prop = new PropVariant { vt = 65 /* VT_BLOB */, blob = new Blob { cbSize = size, pBlobData = blobPtr } };

        int hr;
        IActivateAudioInterfaceAsyncOperation? op = null;
        try
        {
            var clientIid = _iidAudioClient;
            hr = ActivateAudioInterfaceAsync(VirtualProcessLoopback, ref clientIid, ref prop, handler, out op);
            if (hr < 0) throw new COMException("ActivateAudioInterfaceAsync failed", hr);
            if (!handler.Completed.WaitOne(5000)) throw new TimeoutException("Process loopback activation timed out.");
            hr = op.GetActivateResult(out var activateHr, out var activated);
            if (hr < 0 || activateHr < 0) throw new COMException("Process loopback activation returned an error", hr < 0 ? hr : activateHr);
            _client = (IAudioClient)activated;
        }
        finally
        {
            if (op is not null) Marshal.ReleaseComObject(op);
            Marshal.FreeHGlobal(blobPtr);
        }

        hr = _client.GetMixFormat(out var formatPtr);
        if (hr < 0 || formatPtr == IntPtr.Zero) throw new COMException("GetMixFormat failed", hr);
        try
        {
            _format = ParseWaveFormat(formatPtr);
            hr = _client.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback | AudclntStreamFlagsEventCallback, 2000000, 0, formatPtr, Guid.Empty);
            if (hr < 0) throw new COMException("IAudioClient.Initialize failed", hr);
        }
        finally { Marshal.FreeCoTaskMem(formatPtr); }

        _signal = new AutoResetEvent(false);
        hr = _client.SetEventHandle(_signal.SafeWaitHandle.DangerousGetHandle());
        if (hr < 0) throw new COMException("SetEventHandle failed", hr);
        var captureIid = _iidAudioCaptureClient;
        hr = _client.GetService(ref captureIid, out var captureObj);
        if (hr < 0) throw new COMException("GetService(IAudioCaptureClient) failed", hr);
        _capture = (IAudioCaptureClient)captureObj;

        hr = _client.Start();
        if (hr < 0) throw new COMException("IAudioClient.Start failed", hr);

        AppLog.Info("capture: process loopback active pid=" + _pid + " format=" + _format);

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "MeetVaultProcLoopback" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        var blockAlign = Math.Max(1, _format.BlockAlign);
        try
        {
            while (_running)
            {
                if (!_signal!.WaitOne(200)) continue;
                if (_capture!.GetNextPacketSize(out var frames) < 0) continue;
                while (frames > 0 && _running)
                {
                    var hr = _capture.GetBuffer(out var data, out var numFrames, out var flags, out _, out _);
                    if (hr < 0) break;
                    var bytes = (int)numFrames * blockAlign;
                    if (bytes > 0 && (flags & 0x2 /* SILENT */) == 0)
                    {
                        var buffer = new byte[bytes];
                        Marshal.Copy(data, buffer, 0, bytes);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    }
                    _capture.ReleaseBuffer(numFrames);
                    if (_capture.GetNextPacketSize(out frames) < 0) break;
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("capture: process loopback loop ended :: " + ex.Message); }
        finally { RecordingStopped?.Invoke(this, new StoppedEventArgs()); }
    }

    public void StopRecording()
    {
        _running = false;
        try { _client?.Stop(); } catch { }
        _thread?.Join(1000); _thread = null;
    }

    public void Dispose()
    {
        StopRecording();
        if (_capture is not null) { try { Marshal.ReleaseComObject(_capture); } catch { } _capture = null; }
        if (_client is not null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }
        _signal?.Dispose(); _signal = null;
    }

    private static WaveFormat ParseWaveFormat(IntPtr ptr)
    {
        var tag = (ushort)Marshal.ReadInt16(ptr, 0);
        var channels = Marshal.ReadInt16(ptr, 2);
        var rate = Marshal.ReadInt32(ptr, 4);
        var bits = Marshal.ReadInt16(ptr, 14);
        return tag == 3 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, bits, channels);
    }

    // --- interop ---

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams { public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams { public int ActivationType; public AudioClientProcessLoopbackParams ProcessLoopbackParams; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int cbSize; public IntPtr pBlobData; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public Blob blob; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEvent Completed = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr formatPtr, Guid sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
