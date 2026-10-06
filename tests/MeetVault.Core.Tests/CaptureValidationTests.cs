using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class CaptureValidationTests
{
    [Fact]
    public async Task Capture_BadDevice_ThrowsActionable()
    {
        using var cap = new NAudioCapture();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => cap.StartAsync("no-such-device", "", CancellationToken.None));
        Assert.Contains("no-such-device", ex.Message);
    }
}

