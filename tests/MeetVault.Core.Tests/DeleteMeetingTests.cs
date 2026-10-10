using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Deleting a meeting must remove it and its dependent rows, and report its media path.
public sealed class DeleteMeetingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public DeleteMeetingTests() { _repo = new LocalMeetingRepository(_path); }
    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }

    [Fact]
    public void DeleteMeeting_RemovesMeetingSegmentsSpeakers_AndReturnsMedia()
    {
        var m = new Meeting("del-me", "t", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, "/tmp/rec.wav", "audio/wav");
        _repo.Save(m, new[] { new Speaker("spk_1", "Speaker 1") }, new[] { new Segment("seg-1", "spk_1", 0, 1000, "hi", true) });

        var media = _repo.DeleteMeeting("del-me");

        Assert.Equal("/tmp/rec.wav", media);
        Assert.Null(_repo.Load("del-me"));
        Assert.Empty(_repo.LoadSegments("del-me"));
        Assert.Empty(_repo.LoadSpeakers("del-me"));
        Assert.DoesNotContain(_repo.ListMeetings(), x => x.Id == "del-me");
    }
}

