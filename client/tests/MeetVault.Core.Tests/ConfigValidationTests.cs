using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public async Task Session_EmptyMicId_ThrowsActionable()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), new FakeStore());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.StartAsync("", "app"));
        Assert.Contains("mic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

