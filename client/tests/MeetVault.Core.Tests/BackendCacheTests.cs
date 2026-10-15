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
        // Exact path under the cache root, and distortion-free names for the same characters.
        Assert.Equal(Path.Combine(_root, "media", "a9.mp4"), cache.MediaPath("a9", ".mp4"));
        Assert.Equal(Path.Combine(_root, "media", "m_1_A.bin"), cache.MediaPath("m 1/A", ".bin"));
        Assert.NotEqual(cache.MediaPath("m 1/A", ".bin"), cache.MediaPath("m1A", ".bin"));
    }

    [Fact]
    public void ClearAll_RemovesCachedDocumentsAndMedia()
    {
        var cache = new BackendCache(_root);
        cache.SaveAnalysis("m1", "{}");
        cache.SaveTranscript("m1", "{}");
        Assert.NotNull(cache.LoadAnalysis("m1"));
        cache.ClearAll();
        Assert.Null(cache.LoadAnalysis("m1"));
        Assert.False(Directory.Exists(_root));
    }
}
