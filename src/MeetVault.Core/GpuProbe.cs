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

// Capability probe. Each backend reports the devices it can actually drive, in that
// backend's own index order, because that index is what the runtime is handed:
//
//   CUDA   - nvcuda.dll is the driver, and it exports the driver API (cuInit,
//            cuDeviceGetCount, cuDeviceGetName). cudaGetDeviceCount is a runtime API call
//            that lives in cudart, which is absent with only a display driver installed,
//            so probing it reported zero devices on machines that plainly have a GPU.
//            CUDA is offered only when the runtime whisper.net's CUDA build links against
//            (cuBLAS 13) is available AND the installed driver can run CUDA 13, otherwise
//            ggml-cuda cannot load and the picker would offer a backend that silently falls
//            back to CPU. cuBLAS, cuBLASLt and cudart ship with the app (staged under
//            cuda/win-x64 and packaged as Content items, see AGENTS.md), so no toolkit
//            install is needed; the NVIDIA display driver is the one part that cannot be
//            bundled.
//   Vulkan - vulkan-1.dll, enumerated directly. DXGI's adapter index is a different list,
//            so reusing it could hand whisper a Vulkan device index belonging to another
//            card (discrete GPU selected, integrated GPU doing the work and idling).
//
// DXGI is still read as the plain hardware inventory for the log line. Every probe is
// best-effort and returns "unavailable" on failure, never throws.
public static class GpuProbe
{
    private const int DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const uint VendorNvidia = 0x10DE;
    private const uint VendorMicrosoft = 0x1414; // Microsoft Basic Render Driver (WARP)

    // CUDA 13 is what the shipped ggml-cuda native links against, and cuBLAS is redistributed
    // with the app, so CUDA needs no toolkit install. The NVIDIA *driver* is the one part that
    // cannot be bundled, so its version still decides.
    private const int RequiredCudaVersion = 13000;

    private static readonly (int Count, IReadOnlyList<string> Names, int DriverVersion, IReadOnlyList<int> Capabilities) Cuda = ProbeCudaDriver();
    private static readonly bool CudaRuntime = ProbeCudaRuntime();
    private static readonly IReadOnlyList<VulkanAdapter> Vulkan = ProbeVulkan();

    public static IReadOnlyList<GpuAdapter> Adapters { get; } = ProbeDxgiAdapters();
    public static bool VulkanLoaderPresent { get; } = FileExists("vulkan-1.dll");

    static GpuProbe()
    {
        AppLog.Info("gpu: cudaDevices=" + Cuda.Count + " cudaRuntime=" + (CudaRuntime ? "present" : "missing")
            + " vulkanLoader=" + VulkanLoaderPresent + " vulkanDevices=" + Vulkan.Count
            + " dxgiAdapters=" + Adapters.Count + " apis=[" + ApiSummary() + "]");
        foreach (var a in Adapters) AppLog.Info("gpu: dxgi '" + a.Name + "' vendor=0x" + a.VendorId.ToString("X4") + " device=0x" + a.DeviceId.ToString("X4"));
        foreach (var a in Vulkan) AppLog.Info("gpu: vulkan[" + a.Index + "] '" + a.Name + "' vendor=0x" + a.VendorId.ToString("X4") + " device=0x" + a.DeviceId.ToString("X4"));
        foreach (var n in Cuda.Names) AppLog.Info("gpu: cuda device '" + n + "'");
        if (Cuda.Count > 0 && Cuda.DriverVersion > 0)
            AppLog.Info("gpu: cuda driver reports max CUDA " + (Cuda.DriverVersion / 1000) + "." + (Cuda.DriverVersion % 1000 / 10)
                + ", device capabilities [" + string.Join(", ", Cuda.Capabilities.Select(c => "sm_" + (c / 10) + "." + (c % 10))) + "] (devices " + Cuda.Count + ")");
        if (CudaGpuPresent && !CudaRuntime)
            AppLog.Warn("gpu: an NVIDIA GPU is present but the CUDA runtime is missing from this build; CUDA stays unavailable.");
        else if (CudaGpuPresent && !DriverSupportsCuda)
            AppLog.Warn("gpu: CUDA needs driver " + RequiredCudaVersion / 1000 + ".0 or newer, but this driver reports max CUDA "
                + (Cuda.DriverVersion / 1000) + "." + (Cuda.DriverVersion % 1000 / 10) + "; update the NVIDIA driver, or use Vulkan.");
        else if (CudaGpuPresent && !CudaGpuNewEnough)
            AppLog.Warn("gpu: this build's CUDA native carries no code for any device here (it has sm_8.6-8.9 and sm_120/121 only); CUDA stays unavailable, use Vulkan.");
    }

    public static int CudaDeviceCount => Cuda.Count;
    public static bool CudaAvailable => Cuda.Count > 0 && CudaRuntime && DriverSupportsCuda && CudaGpuNewEnough;
    public static bool VulkanAvailable => VulkanLoaderPresent && Vulkan.Count > 0;
    public static bool CudaGpuPresent => Cuda.Count > 0;
    private static bool DriverSupportsCuda => Cuda.DriverVersion >= RequiredCudaVersion;
    // At least one device must be one the shipped native actually has code for, so a machine
    // with only an unsupported card is not offered a CUDA option that can only fail at init.
    private static bool CudaGpuNewEnough => Cuda.Capabilities.Any(CapabilitySupported);

    private static bool DeviceIsNewEnough(int index) =>
        index >= Cuda.Capabilities.Count || CapabilitySupported(Cuda.Capabilities[index]);

    // True when the shipped ggml-cuda native carries code this device can execute. The cubins
    // are sm_86, sm_89, sm_120a and sm_121a, and there is no PTX, so this is the whole set:
    //   - the sm_86 cubin also runs on 8.7, 8.8 and 8.9, because CUDA guarantees a cubin runs on
    //     later minor revisions of its own major. That covers Ampere 8.6 and Ada 8.9 (8.7/8.8
    //     are Jetson Orin), and it is deliberately not widened to 9.x: Hopper 9.0 is a later
    //     major with no code here.
    //   - the two Blackwell cubins are architecture-specific (sm_120a, sm_121a), so they cover
    //     12.0 and 12.1 exactly and are not covered by the forward-compatibility rule. Hence the
    //     explicit minor bound: a future 12.2 or 13.x device is not something this build can run
    //     and must not be offered a CUDA option that only fails at init.
    // Everything else (Turing 7.5, Pascal 6.x, A100 8.0, H100 9.0) is refused rather than
    // offered and then silently fallen back to CPU.
    internal static bool CapabilitySupported(int capability)
    {
        var major = capability / 10;
        var minor = capability % 10;
        if (major == 8) return minor >= 6;
        if (major == 12) return minor <= 1;
        return false;
    }

    // Why the device picker has no GPU to offer, in the user's terms. A GPU that exists but
    // whose runtime is missing is a different problem from having no GPU at all.
    public static string GpuUnavailableReason()
    {
        if (CudaGpuPresent && !CudaRuntime) return "GPU \u2014 CUDA runtime missing from this build";
        if (CudaGpuPresent && !DriverSupportsCuda) return "GPU \u2014 update the NVIDIA driver to " + RequiredCudaVersion / 1000 + ".0 or newer for CUDA, or use Vulkan";
        if (CudaGpuPresent && !CudaGpuNewEnough) return "GPU \u2014 this build's CUDA has no code for this GPU (sm_8.6-8.9 and sm_120/121 only), use Vulkan";
        if (CudaGpuPresent) return "GPU \u2014 CUDA GPU found, but it is not usable right now";
        if (Adapters.Count > 0) return "GPU \u2014 no usable CUDA or Vulkan driver found";
        return "GPU \u2014 no compatible GPU detected";
    }

    // One line for the UI: which GPU backends this PC can actually use for transcription.
    public static string ApiSummary()
    {
        var apis = new List<string> { "CPU" };
        if (CudaAvailable) apis.Add("CUDA");
        if (VulkanAvailable) apis.Add("Vulkan");
        return string.Join(", ", apis);
    }

    // Each GPU offered through a backend it can use, with that backend's own device index.
    // An NVIDIA card is offered as CUDA when CUDA is usable, otherwise as Vulkan, so the
    // same card never appears twice.
    public static IReadOnlyList<GpuDevice> Devices()
    {
        var list = new List<GpuDevice>();
        if (CudaAvailable)
            for (var i = 0; i < Cuda.Count; i++)
            {
                // Do not offer a card this build's CUDA native has no code for; the runtime would
                // only fail at init. The name and index stay aligned with the driver's list.
                if (!DeviceIsNewEnough(i))
                {
                    AppLog.Info("gpu: cuda device " + i + " skipped, sm_" + (Cuda.Capabilities[i] / 10) + "." + (Cuda.Capabilities[i] % 10) + " is outside the sm_8.6-8.9/sm_120-121 set this build ships");
                    continue;
                }
                list.Add(new GpuDevice(GpuBackend.Cuda, i, i < Cuda.Names.Count ? Cuda.Names[i] : "CUDA device " + i));
            }
        foreach (var v in Vulkan)
        {
            if (CudaAvailable && v.VendorId == VendorNvidia) continue; // already offered as CUDA
            list.Add(new GpuDevice(GpuBackend.Vulkan, v.Index, v.Name));
        }
        return list;
    }

    private static bool FileExists(string system32Name)
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, system32Name)); }
        catch { return false; }
    }

    // --- CUDA (driver API through nvcuda.dll) ---

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CuInitFn(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CuDeviceGetCountFn(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CuDeviceGetNameFn(byte[] name, int length, int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CuDriverGetVersionFn(out int version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CuDeviceGetAttributeFn(out int value, int attribute, int device);

    // The shipped ggml-cuda native is SASS-only for a fixed set of architectures, so "newer"
    // is not the test: sm_90 (H100) is newer than sm_86 yet has no code in this build. The
    // embedded cubins are sm_86, sm_89, sm_120a and sm_121a, and CUDA guarantees a cubin runs on
    // later minor revisions of the same major, which covers sm_86 through sm_89. There is no
    // PTX, so nothing can be JIT-compiled for anything else. See CapabilitySupported.
    private const int AttributeComputeCapabilityMajor = 75;
    private const int AttributeComputeCapabilityMinor = 76;

    private static (int Count, IReadOnlyList<string> Names, int DriverVersion, IReadOnlyList<int> Capabilities) ProbeCudaDriver()
    {
        var names = new List<string>();
        var capabilities = new List<int>();
        var lib = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad("nvcuda.dll", out lib)) return (0, names, 0, capabilities);
            if (!NativeLibrary.TryGetExport(lib, "cuInit", out var initPtr)) return (0, names, 0, capabilities);
            if (!NativeLibrary.TryGetExport(lib, "cuDeviceGetCount", out var countPtr)) return (0, names, 0, capabilities);
            // cuInit must run first; every other driver call returns an error before it does.
            if (Marshal.GetDelegateForFunctionPointer<CuInitFn>(initPtr)(0) != 0) return (0, names, 0, capabilities);
            // The highest CUDA version this driver can run, e.g. 13000 for CUDA 13.0.
            var driver = 0;
            if (NativeLibrary.TryGetExport(lib, "cuDriverGetVersion", out var versionPtr))
                Marshal.GetDelegateForFunctionPointer<CuDriverGetVersionFn>(versionPtr)(out driver);
            var getCount = Marshal.GetDelegateForFunctionPointer<CuDeviceGetCountFn>(countPtr);
            if (getCount(out var count) != 0 || count <= 0) return (0, names, driver, capabilities);
            CuDeviceGetNameFn? getName = null;
            if (NativeLibrary.TryGetExport(lib, "cuDeviceGetName", out var namePtr))
                getName = Marshal.GetDelegateForFunctionPointer<CuDeviceGetNameFn>(namePtr);
            CuDeviceGetAttributeFn? getAttribute = null;
            if (NativeLibrary.TryGetExport(lib, "cuDeviceGetAttribute", out var attributePtr))
                getAttribute = Marshal.GetDelegateForFunctionPointer<CuDeviceGetAttributeFn>(attributePtr);
            for (var i = 0; i < count; i++)
            {
                var name = "CUDA device " + i;
                if (getName is not null)
                {
                    var buffer = new byte[128];
                    if (getName(buffer, buffer.Length, i) == 0)
                    {
                        var end = Array.IndexOf(buffer, (byte)0);
                        var text = System.Text.Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
                        if (text.Length > 0) name = text;
                    }
                }
                names.Add(name);
                var capability = 0;
                if (getAttribute is not null
                    && getAttribute(out var major, AttributeComputeCapabilityMajor, i) == 0
                    && getAttribute(out var minor, AttributeComputeCapabilityMinor, i) == 0)
                    capability = major * 10 + minor;
                capabilities.Add(capability);
            }
            return (count, names, driver, capabilities);
        }
        catch (Exception ex)
        {
            AppLog.Warn("gpu: CUDA probe failed :: " + ex.GetType().Name + ": " + ex.Message);
            return (0, names, 0, capabilities);
        }
        finally { if (lib != IntPtr.Zero) NativeLibrary.Free(lib); }
    }

    // cuBLAS 13 is redistributed with the app next to the ggml-cuda native, so this normally
    // resolves to the bundled copy. The system locations stay as a fallback for a machine
    // that has the CUDA toolkit installed. Both DLLs are required: cuBLAS 13 loads through
    // cuBLASLt, so a half-staged pair would pass a single-file check and then fail at init.
    // cudart is checked last, and by bare name only, because that is exactly how whisper.net
    // gates the CUDA runtime (CudaHelper -> NativeLibrary.TryLoad("cudart64_13")). If this
    // probe finds cudart somewhere whisper.net will not, the picker would offer CUDA only for
    // the runtime to fall back to CPU, which is the silent failure this check exists to stop.
    private static bool ProbeCudaRuntime()
    {
        foreach (var name in new[] { "cublas64_13.dll", "cublasLt64_13.dll" })
        {
            var found = false;
            foreach (var path in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "win-x64", name),
                Path.Combine(AppContext.BaseDirectory, name),
            })
            {
                try { if (File.Exists(path)) { found = true; break; } } catch { }
            }
            if (!found && FindOnPath(name) is null) return false;
        }
        return FindOnPath("cudart64_13.dll") is not null;
    }

    // Mirrors the default native search (app folder, then PATH, then the Windows system
    // directory) without loading the library, so startup does not map a half-gigabyte cuBLAS
    // just to answer "is CUDA usable". Kept a superset of the app-folder copy the app ships,
    // so the picker is never stricter than the runtime's own bare-name load.
    private static string? FindOnPath(string fileName)
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(local)) return local;
            foreach (var part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (part.Length == 0) continue;
                var candidate = Path.Combine(part, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            var system = Path.Combine(Environment.SystemDirectory, fileName);
            if (File.Exists(system)) return system;
            var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), fileName);
            if (File.Exists(windows)) return windows;
            var cwd = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            return File.Exists(cwd) ? cwd : null;
        }
        catch { return null; }
    }

    // --- Vulkan (vulkan-1.dll) ---

    private sealed record VulkanAdapter(int Index, string Name, uint VendorId, uint DeviceId);

    // Mirrors ggml-vulkan's own device filter: only discrete and integrated GPUs reach its
    // device list, and therefore whisper.cpp's gpu_device index. Software rasterizers and
    // virtual adapters are dropped before whisper can ever select them.
    internal static bool VulkanDeviceUsable(int deviceType) =>
        deviceType == VkPhysicalDeviceTypeDiscreteGpu || deviceType == VkPhysicalDeviceTypeIntegratedGpu;

    private const uint VkStructureTypeApplicationInfo = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    private const int VkSuccess = 0;
    private const int VkIncomplete = 5;
    // ggml-vulkan builds its device list from these two types only (its own filter is
    // "eDiscreteGpu || eIntegratedGpu"), so an index counted over the raw loader order points
    // at the wrong card as soon as any other type (software rasterizer, virtual adapter) sits
    // ahead of it.
    private const int VkPhysicalDeviceTypeIntegratedGpu = 1;
    private const int VkPhysicalDeviceTypeDiscreteGpu = 2;
    // VkPhysicalDeviceProperties is ~816 bytes; the call writes all of it into the buffer.
    private const int VkPropertiesBufferBytes = 2048;

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint SType; public IntPtr PNext; public IntPtr PApplicationName; public uint ApplicationVersion;
        public IntPtr PEngineName; public uint EngineVersion; public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType; public IntPtr PNext; public uint Flags; public IntPtr PApplicationInfo;
        public uint EnabledLayerCount; public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount; public IntPtr PpEnabledExtensionNames;
    }

    // Only the leading fields are read; the rest of the native struct stays in the buffer.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct VkPhysicalDevicePropertiesHead
    {
        public uint ApiVersion; public uint DriverVersion; public uint VendorId; public uint DeviceId; public int DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DeviceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VkCreateInstanceFn(IntPtr createInfo, IntPtr allocator, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VkEnumeratePhysicalDevicesFn(IntPtr instance, ref uint count, IntPtr devices);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VkGetPhysicalDevicePropertiesFn(IntPtr physicalDevice, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VkDestroyInstanceFn(IntPtr instance, IntPtr allocator);

    private static IReadOnlyList<VulkanAdapter> ProbeVulkan()
    {
        var list = new List<VulkanAdapter>();
        var lib = IntPtr.Zero;
        var appInfo = IntPtr.Zero;
        var createInfo = IntPtr.Zero;
        var devices = IntPtr.Zero;
        var props = IntPtr.Zero;
        var instance = IntPtr.Zero;
        VkDestroyInstanceFn? destroy = null;
        try
        {
            if (!NativeLibrary.TryLoad("vulkan-1.dll", out lib)) return list;
            if (!NativeLibrary.TryGetExport(lib, "vkCreateInstance", out var createPtr)) return list;
            if (!NativeLibrary.TryGetExport(lib, "vkEnumeratePhysicalDevices", out var enumPtr)) return list;
            if (!NativeLibrary.TryGetExport(lib, "vkGetPhysicalDeviceProperties", out var propsPtr)) return list;
            var create = Marshal.GetDelegateForFunctionPointer<VkCreateInstanceFn>(createPtr);
            var enumerate = Marshal.GetDelegateForFunctionPointer<VkEnumeratePhysicalDevicesFn>(enumPtr);
            var getProps = Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDevicePropertiesFn>(propsPtr);
            if (NativeLibrary.TryGetExport(lib, "vkDestroyInstance", out var destroyPtr))
                destroy = Marshal.GetDelegateForFunctionPointer<VkDestroyInstanceFn>(destroyPtr);

            var app = new VkApplicationInfo { SType = VkStructureTypeApplicationInfo, ApiVersion = 1u << 22 }; // Vulkan 1.0
            appInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(app, appInfo, false);
            var ci = new VkInstanceCreateInfo { SType = VkStructureTypeInstanceCreateInfo, PApplicationInfo = appInfo };
            createInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkInstanceCreateInfo>());
            Marshal.StructureToPtr(ci, createInfo, false);
            if (create(createInfo, IntPtr.Zero, out instance) != VkSuccess || instance == IntPtr.Zero)
            {
                AppLog.Info("gpu: vulkan instance creation failed (no Vulkan driver installed)");
                return list;
            }

            uint count = 0;
            var result = enumerate(instance, ref count, IntPtr.Zero);
            if ((result != VkSuccess && result != VkIncomplete) || count == 0)
            {
                AppLog.Info("gpu: vulkan reported no physical devices (result=" + result + ")");
                return list;
            }
            devices = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            result = enumerate(instance, ref count, devices);
            if (result != VkSuccess && result != VkIncomplete) return list;
            props = Marshal.AllocHGlobal(VkPropertiesBufferBytes);
            var gpuIndex = 0;
            for (var i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(devices, i * IntPtr.Size);
                getProps(device, props);
                var head = Marshal.PtrToStructure<VkPhysicalDevicePropertiesHead>(props);
                if (!VulkanDeviceUsable(head.DeviceType)) continue; // ggml keeps only discrete/integrated GPUs
                var name = (head.DeviceName ?? "").Trim();
                if (name.Length == 0) name = "Vulkan device " + i;
                // Numbered over the devices ggml keeps, in loader order, because whisper.cpp's
                // gpu_device counts GPU/IGPU entries in ggml's list, not physical-device indices.
                // Without this an integrated GPU sitting behind a filtered-out adapter gets the
                // wrong index, whisper finds no usable GPU, and the CPU does the work while the
                // picker still shows the card as chosen.
                list.Add(new VulkanAdapter(gpuIndex++, name, head.VendorId, head.DeviceId));
                // ponytail: ggml also drops devices lacking storageBuffer16BitAccess and merges
                // duplicate-driver entries; both are rare on Windows, so the count can still drift
                // there. Query VkPhysicalDeviceVulkan11Features if that ever bites.
            }
            return list;
        }
        catch (Exception ex)
        {
            AppLog.Warn("gpu: Vulkan probe failed :: " + ex.GetType().Name + ": " + ex.Message);
            return list;
        }
        finally
        {
            if (instance != IntPtr.Zero && destroy is not null) { try { destroy(instance, IntPtr.Zero); } catch { } }
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (devices != IntPtr.Zero) Marshal.FreeHGlobal(devices);
            if (createInfo != IntPtr.Zero) Marshal.FreeHGlobal(createInfo);
            if (appInfo != IntPtr.Zero) Marshal.FreeHGlobal(appInfo);
            if (lib != IntPtr.Zero) NativeLibrary.Free(lib);
        }
    }

    // --- DXGI interop (hardware inventory for the log) ---

    private static IReadOnlyList<GpuAdapter> ProbeDxgiAdapters()
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
        catch (Exception ex)
        {
            AppLog.Warn("gpu: DXGI adapter query failed :: " + ex.GetType().Name + ": " + ex.Message);
            return Array.Empty<GpuAdapter>();
        }
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
