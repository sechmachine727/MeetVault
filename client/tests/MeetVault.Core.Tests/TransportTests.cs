using MeetVault.Core;
using System.Net;

namespace MeetVault.Core.Tests;

sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _queue = new();
    public readonly List<string> Urls = new();
    public void Enqueue(string json, HttpStatusCode code = HttpStatusCode.OK) => _queue.Enqueue(new HttpResponseMessage(code) { Content = new StringContent(json) });
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Urls.Add(req.RequestUri!.ToString());
        return Task.FromResult(_queue.Dequeue());
    }
}

public sealed class TransportTests
{
    [Fact]
    public async Task Upload_PollsUntilActive_ThenInteracts()
    {
        var h = new ScriptedHandler();
        h.Enqueue("{\"file\":{\"name\":\"files/1\",\"state\":\"PROCESSING\"}}");
        h.Enqueue("{\"name\":\"files/1\",\"state\":\"PROCESSING\"}");
        h.Enqueue("{\"name\":\"files/1\",\"state\":\"ACTIVE\"}");
        h.Enqueue("{\"result\":\"ok\"}");
        using var tx = new GeminiHttpTransport("KEY", http: new HttpClient(h));
        var id = await tx.UploadFileAsync("m.webm", new MemoryStream(new byte[10]), "video/webm", CancellationToken.None);
        Assert.Equal("files/1", id);
        Assert.Equal("{\"result\":\"ok\"}", await tx.InteractAsync(id, "[]", CancellationToken.None));
        Assert.DoesNotContain(h.Urls, u => u.Contains("KEY") == false);
    }

    [Fact]
    public async Task Upload_FailedState_Throws()
    {
        var h = new ScriptedHandler();
        h.Enqueue("{\"file\":{\"name\":\"files/9\",\"state\":\"PROCESSING\"}}");
        h.Enqueue("{\"name\":\"files/9\",\"state\":\"FAILED\"}");
        using var tx = new GeminiHttpTransport("KEY", http: new HttpClient(h));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UploadFileAsync("m.webm", new MemoryStream(new byte[10]), "video/webm", CancellationToken.None));
    }
}

