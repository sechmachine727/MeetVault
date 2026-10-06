using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class DeviceListTests
{
    [Fact]
    public void ListDevices_DoesNotThrow()
    {
        var devices = NAudioCapture.ListDevices();
        Assert.NotNull(devices);
    }

    [Fact]
    public void Whisper_CpuFactoryOptions_DefaultOff()
    {
        using var tx = new WhisperTranscriber("models/does-not-exist.bin", "auto", useGpu: false);
        Assert.False(tx.UseGpu);
    }
}
