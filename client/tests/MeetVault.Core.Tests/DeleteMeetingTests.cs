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

    [Theory]
    [InlineData(MeetingStatus.Recording)]
    [InlineData(MeetingStatus.Paused)]
    public void DeleteMeeting_ActiveOrResumable_Refused_RowKept(MeetingStatus status)
    {
        // One shared rule with the bulk path: a resumable draft cannot be deleted through
        // the repository either (stop or discard it in Live first) — same message the
        // bulk path reports for the same state.
        var m = new Meeting("draft", "t", DateTimeOffset.UtcNow, null, status, "/tmp/rec.wav", "audio/wav");
        _repo.Save(m, new[] { new Speaker("spk_1", "Speaker 1") }, Array.Empty<Segment>());

        var ex = Assert.Throws<InvalidOperationException>(() => _repo.DeleteMeeting("draft"));

        Assert.Equal("active or resumable recording", ex.Message);
        Assert.NotNull(_repo.Load("draft"));
    }
}

