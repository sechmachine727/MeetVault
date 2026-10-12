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
}
