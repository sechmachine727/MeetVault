using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Synthetic long-session run per 05-test-plan: 70 minutes of 10 s windows through session + real repo.
public sealed class LongSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private readonly LocalMeetingRepository _repo;
    public LongSessionTests() { _repo = new LocalMeetingRepository(_path); }

    [Fact]
    public async Task SeventyMinutes_NoResetOrDrift()
    {
        var tx = new RawTranscriber();
        var session = new MeetingSession(new FakeAudio(), tx, _repo);
        await session.StartAsync("mic", "app");
        const int windows = 420; // 70 min x 10 s
        for (var i = 0; i < windows; i++)
        {
            var start = (long)i * 10_000;
            tx.Emit(new Segment($"s{i}", "spk_1", start, start + 10_000, $"line {i}", true));
        }
        await session.StopAsync();
        var segs = _repo.LoadSegments(_repo.ListMeetings().Single().Id);
        Assert.Equal(windows, segs.Count);
        for (var i = 1; i < segs.Count; i++)
            Assert.True(segs[i].StartMs >= segs[i - 1].EndMs, $"reset/drift at index {i}");
        Assert.Equal(4_200_000L, segs[^1].EndMs - segs[0].StartMs);
    }

    public void Dispose() { _repo.Dispose(); try { File.Delete(_path); } catch { } }
}

