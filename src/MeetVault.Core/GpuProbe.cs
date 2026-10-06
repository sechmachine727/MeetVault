using System.Runtime.InteropServices;

namespace MeetVault.Core;

public enum GpuBackend { Cpu, Cuda, Vulkan }

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

// True capability probe, not a vendor-name guess. CUDA asks the driver how many
// devices it exposes; the GPU list comes from the display adapters the OS actually
// has; Vulkan is offered only when a hardware GPU and the Vulkan loader are present.
// Every probe is best-effort and returns "unavailable" on failure, never throws.
public static class GpuProbe
{
    public static int CudaDeviceCount { get; } = ProbeCuda();
    public static IReadOnlyList<string> Adapters { get; } = ProbeAdapters();

    public static bool CudaAvailable => CudaDeviceCount > 0;
    public static bool VulkanAvailable => Adapters.Count > 0 && File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    // Every usable GPU, in preference order (CUDA first, matching whisper's own order).
    public static IReadOnlyList<GpuDevice> Devices()
    {
        var list = new List<GpuDevice>();
        for (var i = 0; i < CudaDeviceCount; i++) list.Add(new GpuDevice(GpuBackend.Cuda, i, $"CUDA device {i}"));
        if (VulkanAvailable)
            for (var i = 0; i < Adapters.Count; i++) list.Add(new GpuDevice(GpuBackend.Vulkan, i, Adapters[i]));
        return list;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    // Real display adapters, excluding software renderers (Basic Render Driver / WARP).
    private static IReadOnlyList<string> ProbeAdapters()
    {
        try
        {
            var names = new List<string>();
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                var name = (dd.DeviceString ?? "").Trim();
                if (name.Length == 0 || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("WARP", StringComparison.OrdinalIgnoreCase))
                    continue;
                names.Add(name);
            }
            return names;
        }
        catch { return Array.Empty<string>(); }
    }
}

