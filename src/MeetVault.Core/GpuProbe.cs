using System.Runtime.InteropServices;

namespace MeetVault.Core;

public enum GpuBackend { Cpu, Cuda, Vulkan }

public sealed record GpuAdapter(string Name, uint VendorId, uint DeviceId);
public sealed record GpuDevice(GpuBackend Backend, int Index, string Name);

// Persisted form of the device choice: "cpu", "cuda:N", "vulkan:N".
// Legacy "gpu" (from before real detection) maps to the first available GPU.
public static class TranscriptionDevice
{
    public static string Encode(GpuBackend backend, int index) => backend switch
    {
        GpuBackend.Cuda => "cuda:" + index,
        GpuBackend.Vulkan => "vulkan:" + index,
        _ => "cpu",
    };

    public static (GpuBackend Backend, int Index) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "cpu") return (GpuBackend.Cpu, 0);
        var parts = value.Split(':');
        var backend = parts[0] switch
        {
            "cuda" => GpuBackend.Cuda,
            "vulkan" => GpuBackend.Vulkan,
            "gpu" => GpuProbe.CudaAvailable ? GpuBackend.Cuda : GpuProbe.VulkanAvailable ? GpuBackend.Vulkan : GpuBackend.Cpu,
            _ => GpuBackend.Cpu,
        };
        var index = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0;
        return (backend, index);
    }
}

// True capability probe. GPUs come from DXGI (EnumAdapters1), which lists the real
// hardware adapters a compute runtime can use and excludes indirect/virtual display
// drivers (Sudomaker, Horizon IDD, Hyper-V Video) and WARP. Graphics/inference APIs are
// reported separately by probing the runtime loaders. Every probe is best-effort and
// returns "unavailable" on failure, never throws.
public static class GpuProbe
{
    static GpuProbe()
    {
        AppLog.Info("gpu: cuda devices=" + CudaDeviceCount + " adapters=" + Adapters.Count + " vulkanLoader=" + VulkanLoaderPresent + " apis=[" + ApiSummary() + "]");
        foreach (var a in Adapters) AppLog.Info("gpu: adapter '" + a.Name + "' vendor=0x" + a.VendorId.ToString("X4") + " device=0x" + a.DeviceId.ToString("X4"));
    }

    private const int DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const uint VendorNvidia = 0x10DE;
    private const uint VendorMicrosoft = 0x1414; // Microsoft Basic Render Driver (WARP)

    public static int CudaDeviceCount { get; } = ProbeCuda();
    public static IReadOnlyList<GpuAdapter> Adapters { get; } = ProbeAdapters();
    public static bool VulkanLoaderPresent { get; } = FileExists("vulkan-1.dll");
    public static bool CudaAvailable => CudaDeviceCount > 0;
    public static bool VulkanAvailable => VulkanLoaderPresent && Adapters.Count > 0;

    // One line for the UI: which GPU backends this PC can actually use for transcription.
    // Only the backends Whisper.net can load are listed, so the line matches the device
    // picker (CPU, CUDA, Vulkan). DirectML is not a Whisper.net backend, so it is omitted.
    public static string ApiSummary()
    {
        var apis = new List<string> { "CPU" };
        if (CudaAvailable) apis.Add("CUDA");
        if (VulkanAvailable) apis.Add("Vulkan");
        return string.Join(", ", apis);
    }

    // Each GPU offered through a backend it can actually use: NVIDIA with the CUDA runtime
    // runs on CUDA, otherwise the card is offered through Vulkan. No duplicate NVIDIA entry.
    public static IReadOnlyList<GpuDevice> Devices()
    {
        var list = new List<GpuDevice>();
        var nvidia = Adapters.FirstOrDefault(a => a.VendorId == VendorNvidia);
        if (CudaAvailable)
        {
            var name = nvidia?.Name ?? "NVIDIA GPU";
            for (var i = 0; i < CudaDeviceCount; i++) list.Add(new GpuDevice(GpuBackend.Cuda, i, name));
        }
        if (VulkanAvailable)
        {
            for (var i = 0; i < Adapters.Count; i++)
            {
                var a = Adapters[i];
                if (CudaAvailable && a.VendorId == VendorNvidia) continue; // already offered as CUDA
                list.Add(new GpuDevice(GpuBackend.Vulkan, i, a.Name));
            }
        }
        return list;
    }

    private static bool FileExists(string system32Name)
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, system32Name)); }
        catch { return false; }
    }

    [DllImport("nvcuda.dll", EntryPoint = "cudaGetDeviceCount", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CudaGetDeviceCountNative(out int count);

    private static int ProbeCuda()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvcuda.dll", out var lib)) return 0;
            try
            {
                if (!NativeLibrary.TryGetExport(lib, "cudaGetDeviceCount", out var fn)) return 0;
                var get = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(fn);
                return get(out var count) == 0 ? count : 0; // 0 == cudaSuccess
            }
            finally { NativeLibrary.Free(lib); }
        }
        catch { return 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGetDeviceCount(out int count);

    // --- DXGI interop (the API that lists real, compute-capable hardware adapters) ---

    private static IReadOnlyList<GpuAdapter> ProbeAdapters()
    {
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factoryPtr) != 0 || factoryPtr == IntPtr.Zero) return Array.Empty<GpuAdapter>();

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                var list = new List<GpuAdapter>();
                var seen = new HashSet<(uint, uint)>();
                for (uint i = 0; ; i++)
                {
                    if (factory.EnumAdapters1(i, out var adapter) != 0) break;
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue; // WARP / Basic Render Driver
                        if (d.VendorId == VendorMicrosoft) continue;
                        if (!seen.Add((d.VendorId, d.DeviceId))) continue; // DXGI can repeat one card per output
                        var name = (d.Description ?? "").Trim();
                        if (name.Length == 0) continue;
                        // Documented fallback: the Basic Render Driver can appear without the
                        // software flag, so reject it by name too.
                        if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new GpuAdapter(name, d.VendorId, d.DeviceId));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
                return list;
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return Array.Empty<GpuAdapter>(); }
        finally { if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr); }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr data);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr output1);
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr data);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapter1);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapter1);
        [PreserveSig] int IsCurrent();
    }
}
