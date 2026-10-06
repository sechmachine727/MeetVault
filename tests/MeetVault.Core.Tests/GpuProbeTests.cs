using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Device setting encoding plus the real capability probe. The probe touches native
// loaders, so this pins that it never throws and that encode/parse round-trips.
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
        Assert.False(string.IsNullOrWhiteSpace(TranscriptionDevice.Encode(b, 0)));
    }

    [Fact]
    public void Probe_NeverThrows_AndCountsAreConsistent()
    {
        Assert.True(GpuProbe.CudaDeviceCount >= 0);
        var devices = GpuProbe.Devices();
        var expected = GpuProbe.CudaDeviceCount + (GpuProbe.VulkanAvailable ? GpuProbe.Adapters.Count : 0);
        Assert.Equal(expected, devices.Count);
        Assert.Equal(GpuProbe.CudaDeviceCount > 0, GpuProbe.CudaAvailable);
    }
}
