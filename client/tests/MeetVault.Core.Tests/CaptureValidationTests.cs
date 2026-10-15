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

    // A recovery that fails after capture opened parks the crashed file in the resume path,
    // not the reuse path. Clearing only the reuse path would send the next meeting there.
    [Fact]
    public void ClearReuse_ForgetsBothReusePaths()
    {
        using var cap = new NAudioCapture();
        cap.ReuseFile(Path.Combine(Path.GetTempPath(), "crashed-meeting.wav"));
        // A recovery that failed after capture opened parks the file in the resume path, which
        // only StopCore sets, so put the instance in that state directly.
        typeof(NAudioCapture)
            .GetField("_resumePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(cap, Path.Combine(Path.GetTempPath(), "resumed-meeting.wav"));
        Assert.NotNull(cap.MediaPath);
        cap.ClearReuse();
        Assert.Null(cap.MediaPath);
    }
}
