using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Device setting encoding plus the probe's shape. The probe touches native loaders and
// DXGI, so this pins that it never throws and that device entries are consistent.
public sealed class GpuProbeTests
{
    [Theory]
    [InlineData("cpu", GpuBackend.Cpu, 0)]
    [InlineData("cuda:1", GpuBackend.Cuda, 1)]
    [InlineData("vulkan:2", GpuBackend.Vulkan, 2)]
    public void Parse_ReadsBackendAndIndex(string value, GpuBackend backend, int index)
    {
        var (b, i) = TranscriptionDevice.Parse(value);
        Assert.Equal(backend, b);
        Assert.Equal(index, i);
    }

    [Fact]
    public void Encode_RoundTrips()
    {
        Assert.Equal("cpu", TranscriptionDevice.Encode(GpuBackend.Cpu, 0));
        Assert.Equal("cuda:0", TranscriptionDevice.Encode(GpuBackend.Cuda, 0));
        Assert.Equal("vulkan:1", TranscriptionDevice.Encode(GpuBackend.Vulkan, 1));
        Assert.Equal((GpuBackend.Vulkan, 1), TranscriptionDevice.Parse("vulkan:1"));
    }

    [Fact]
    public void LegacyGpuValue_MapsToAvailableBackend()
    {
        // Old builds stored the bare word "gpu"; it must still resolve without throwing.
        var (b, _) = TranscriptionDevice.Parse("gpu");
        Assert.True(b is GpuBackend.Cpu or GpuBackend.Cuda or GpuBackend.Vulkan);
    }

    [Fact]
    public void Devices_AreGpuOnly_AndUnique()
    {
        var devices = GpuProbe.Devices();
        // No CPU entry appears in the GPU list, and no backend:index repeats.
        Assert.DoesNotContain(devices, d => d.Backend == GpuBackend.Cpu);
        Assert.Equal(devices.Count, devices.Select(d => (d.Backend, d.Index)).Distinct().Count());
        // A GPU offered as CUDA is not also offered as Vulkan.
        Assert.DoesNotContain(devices, d => d.Backend == GpuBackend.Cuda && devices.Any(v => v.Backend == GpuBackend.Vulkan && v.Name == d.Name));
    }

    [Fact]
    public void Probe_NeverThrows()
    {
        Assert.True(GpuProbe.CudaDeviceCount >= 0);
        Assert.NotNull(GpuProbe.Adapters);
        Assert.False(string.IsNullOrWhiteSpace(GpuProbe.ApiSummary()));
    }
}

