using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public async Task Session_EmptyMicId_ThrowsActionable()
    {
        var session = new MeetingSession(new FakeAudio(), new RawTranscriber(), new FakeDiarizer(), new FakeGender(), new FakeStore());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.StartAsync("", "app"));
        Assert.Contains("mic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Whisper_UnsupportedLanguage_ThrowsActionable()
    {
        var ex = Assert.Throws<ArgumentException>(() => new WhisperTranscriber("models/ggml-base.bin", "klingon"));
        Assert.Contains("klingon", ex.Message);
    }
}

