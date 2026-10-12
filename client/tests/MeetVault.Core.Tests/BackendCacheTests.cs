using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public sealed class BackendCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mvb-cache-" + Guid.NewGuid().ToString("n"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Transcript_RoundTrips_And_LeavesNoTempFile()
    {
        var cache = new BackendCache(_root);
        Assert.Null(cache.LoadTranscript("m 1/A"));
        cache.SaveTranscript("m 1/A", "{\"a\":1}");
        Assert.Equal("{\"a\":1}", cache.LoadTranscript("m 1/A"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Analysis_RoundTrips_And_MediaPath_IsStable()
    {
        var cache = new BackendCache(_root);
        cache.SaveAnalysis("mv1", "{}");
        Assert.Equal("{}", cache.LoadAnalysis("mv1"));
        Assert.EndsWith("a9.mp4", cache.MediaPath("a9", ".mp4"));
        Assert.Contains("media", cache.MediaPath("a9", ".mp4"));
    }
}
