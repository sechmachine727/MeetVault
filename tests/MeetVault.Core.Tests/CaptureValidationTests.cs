using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Capture is always a combination of mic and system audio; the source hints select
// which parts are active. These pin the validation and the no-source case.
public sealed class CaptureValidationTests
{
    [Fact]
    public async Task Capture_BadDevice_ThrowsActionable()
    {
        using var cap = new NAudioCapture();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => cap.StartAsync("no-such-device", "", CancellationToken.None));
        Assert.Contains("no-such-device", ex.Message);
    }

    [Fact]
    public async Task Capture_AllSourcesOff_Throws()
    {
        using var cap = new NAudioCapture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cap.StartAsync("off", "off", CancellationToken.None));
    }

    [Fact]
    public void ListAudioApps_DoesNotThrow()
    {
        var apps = NAudioCapture.ListAudioApps();
        Assert.NotNull(apps);
    }
}

