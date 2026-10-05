using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class RepositoryReadTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public RepositoryReadTests() { _repo = new LocalMeetingRepository(_path); }
    private static Meeting M(string id, MeetingStatus st) => new(id, "T-" + id, DateTimeOffset.UtcNow.AddHours(-1), st == MeetingStatus.Ready ? DateTimeOffset.UtcNow : null, st, st == MeetingStatus.Ready ? "r.webm" : null, null);

    [Fact]
    public void LoadSegments_ReturnsInOrder()
    {
        _repo.Save(M("m1", MeetingStatus.Ready), new[] { new Speaker("spk_1", "Male 1", Gender.Male, 1) }, new[] { new Segment("s2", "spk_1", 1000, 2000, "two", true), new Segment("s1", "spk_1", 0, 1000, "one", true) });
        var segs = _repo.LoadSegments("m1");
        Assert.Equal(new[] { "s1", "s2" }, segs.Select(s => s.Id));
    }

    [Fact]
    public void LoadSpeakers_ReturnsRenamed()
    {
        _repo.Save(M("m1", MeetingStatus.Ready), new[] { new Speaker("spk_1", "Male 1", Gender.Male, 1) }, Array.Empty<Segment>());
        _repo.RenameSpeaker("m1", "spk_1", "Bob");
        Assert.Equal("Bob", _repo.LoadSpeakers("m1").Single().DisplayName);
    }

    [Fact]
    public void ListMeetings_ReturnsNewestFirst()
    {
        _repo.Save(M("old", MeetingStatus.Ready), Array.Empty<Speaker>(), Array.Empty<Segment>());
        _repo.Save(M("new", MeetingStatus.Recording), Array.Empty<Speaker>(), Array.Empty<Segment>());
        Assert.Equal(new[] { "new", "old" }, _repo.ListMeetings().Select(m => m.Id));
    }

    [Fact]
    public void LoadUnfinished_ReturnsRecordingAndPaused()
    {
        _repo.Save(M("done", MeetingStatus.Ready), Array.Empty<Speaker>(), Array.Empty<Segment>());
        _repo.Save(M("rec", MeetingStatus.Recording), Array.Empty<Speaker>(), Array.Empty<Segment>());
        _repo.Save(M("pau", MeetingStatus.Paused), Array.Empty<Speaker>(), Array.Empty<Segment>());
        Assert.Equal(new[] { "pau", "rec" }.OrderBy(x => x), _repo.LoadUnfinished().Select(m => m.Id).OrderBy(x => x));
    }

    [Fact]
    public void GeminiJob_SaveLoad_RoundTrip()
    {
        _repo.SaveJob(new GeminiJob("j1", "m1", GeminiJobState.Failed, 2, "boom", null));
        var job = _repo.LoadJob("m1");
        Assert.NotNull(job);
        Assert.Equal((GeminiJobState.Failed, 2, "boom"), (job.State, job.Attempts, job.LastError));
        _repo.SaveJob(job with { State = GeminiJobState.Succeeded, ResultPath = "r.json" });
        Assert.Equal("r.json", _repo.LoadJob("m1")!.ResultPath);
    }

    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }
}

