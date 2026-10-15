using MeetVault.Core;

namespace MeetVault.Core.Tests;

// FR9: settings must round-trip through the store; a renamed/typo'd key would
// silently break a wiring (e.g. diarization) with no test failure.
public sealed class SettingsRoundTripTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public SettingsRoundTripTests() { _repo = new LocalMeetingRepository(_path); }
    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }

    [Fact]
    public void SetGet_Overwrite_AndMissing()
    {
        Assert.Null(_repo.GetSetting("transcription.diarization"));
        _repo.SetSetting("transcription.diarization", "off");
        Assert.Equal("off", _repo.GetSetting("transcription.diarization"));
        _repo.SetSetting("transcription.diarization", "on");
        Assert.Equal("on", _repo.GetSetting("transcription.diarization"));
    }
}

