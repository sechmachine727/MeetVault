using MeetVault.Core;

namespace MeetVault.Core.Tests;

sealed class FakeSecrets : ISecretStore
{
    public string? Key;
    public string? Get(string key) => Key;
    public void Set(string key, string value) => Key = value;
}

sealed class FakeFiles : IMeetingFileReader
{
    public long Size; public byte[] Content = "fake-media"u8.ToArray();
    public long SizeOf(string path) => Size;
    public Stream OpenRead(string path) => new MemoryStream(Content);
}

sealed class FakeTransport : IGeminiTransport
{
    public bool Throw;
    public Task<string> UploadFileAsync(string path, Stream content, string mime, CancellationToken ct) => Task.FromResult("file_1");
    public Task<string> InteractAsync(string fileId, string transcriptJson, CancellationToken ct) => Throw ? throw new HttpRequestException("boom") : Task.FromResult("{\"summary\":\"ok\"}");
}

public sealed class GeminiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
    private Meeting M(string? media = "rec.webm") => new("m1", "T", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, media, "video/webm");

    [Fact]
    public async Task Success_StoresResult()
    {
        var up = new GeminiUploader(new FakeSecrets { Key = "k" }, new FakeFiles(), new FakeTransport());
        var job = await up.SendMeetingAsync(M(), "[]", _dir);
        Assert.Equal(GeminiJobState.Succeeded, job.State);
        Assert.NotNull(job.ResultPath);
        Assert.True(File.Exists(job.ResultPath));
    }

    [Fact]
    public async Task NoKey_Fails_WithGuidance()
    {
        var up = new GeminiUploader(new FakeSecrets(), new FakeFiles(), new FakeTransport());
        var job = await up.SendMeetingAsync(M(), "[]", _dir);
        Assert.Equal(GeminiJobState.Failed, job.State);
        Assert.Contains("API key", job.LastError);
    }

    [Fact]
    public async Task Over2GB_RefusedBeforeUpload()
    {
        var up = new GeminiUploader(new FakeSecrets { Key = "k" }, new FakeFiles { Size = GeminiUploader.MaxFileBytes + 1 }, new FakeTransport());
        var job = await up.SendMeetingAsync(M(), "[]", _dir);
        Assert.Equal(GeminiJobState.Failed, job.State);
        Assert.Contains("2 GB", job.LastError);
    }

    [Fact]
    public async Task TransportFailure_PreservesOriginals()
    {
        var up = new GeminiUploader(new FakeSecrets { Key = "k" }, new FakeFiles(), new FakeTransport { Throw = true });
        var job = await up.SendMeetingAsync(M(), "[]", _dir);
        Assert.Equal(GeminiJobState.Failed, job.State);
        Assert.Null(job.ResultPath);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}

