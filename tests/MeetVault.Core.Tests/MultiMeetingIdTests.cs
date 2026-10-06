using MeetVault.Core;

namespace MeetVault.Core.Tests;

// transcript_segments.id is a global primary key; ids are namespaced by meeting id
// ({meetingId}-w{start}-{index}), so two meetings must both save.
public sealed class MultiMeetingIdTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public MultiMeetingIdTests() { _repo = new LocalMeetingRepository(_path); }
    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }

    private static Meeting M(string id) => new(id, "t", DateTimeOffset.UtcNow, null, MeetingStatus.Ready, null, null);

    [Fact]
    public void TwoMeetings_PerMeetingIds_NoCollision()
    {
        _repo.Save(M("a"), Array.Empty<Speaker>(), new[] { new Segment("a-w0-0", "spk_1", 0, 6000, "one", true) });
        _repo.Save(M("b"), Array.Empty<Speaker>(), new[] { new Segment("b-w0-0", "spk_1", 0, 6000, "two", true) });
        Assert.Single(_repo.LoadSegments("a"));
        Assert.Single(_repo.LoadSegments("b"));
    }

    [Fact]
    public void SameIdInTwoMeetings_IsRejected()
    {
        _repo.Save(M("a"), Array.Empty<Speaker>(), new[] { new Segment("live-w0-0", "spk_1", 0, 6000, "one", true) });
        // The old hardcoded "live" namespace caused exactly this collision.
        Assert.ThrowsAny<Exception>(() =>
            _repo.Save(M("b"), Array.Empty<Speaker>(), new[] { new Segment("live-w0-0", "spk_1", 0, 6000, "two", true) }));
    }
}

