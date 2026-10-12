using System.Net;
using System.Text;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

// One scripted HTTP handler drives every transport behavior; request capture proves verbs,
// URIs, headers and bodies without a network.
internal sealed class BackendScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scripted = new();
    public List<(string Method, string Uri, string? Auth, string? Body)> Requests { get; } = new();

    public void Enqueue(HttpStatusCode status, string body, string contentType = "application/json")
        => _scripted.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(), body));
        if (_scripted.Count == 0) throw new InvalidOperationException("no scripted response left");
        return _scripted.Dequeue()(request);
    }
}

public sealed class BackendTransportTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "mvb-transport-" + Guid.NewGuid().ToString("n") + ".bin");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Send_AddsBearerHeader_BuildsQuery_ReturnsBody()
    {
        var handler = new BackendScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        using var transport = new BackendHttpTransport("http://localhost:8080", "k-123", handler);
        var body = await transport.SendAsync(new BackendRequest("GET", "/v1/meetings", new Dictionary<string, string> { ["limit"] = "5", ["offset"] = "0" }), default);
        Assert.Equal("{\"ok\":true}", body);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("http://localhost:8080/v1/meetings?limit=5&offset=0", handler.Requests[0].Uri);
        Assert.Equal("Bearer k-123", handler.Requests[0].Auth);
    }

    [Fact]
    public async Task Send_MapsErrorEnvelope_ToBackendApiException()
    {
        var handler = new BackendScriptedHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"bad key\",\"detail\":{}},\"request_id\":\"req-9\"}");
        using var transport = new BackendHttpTransport("http://localhost:8080", "k", handler);
        var ex = await Assert.ThrowsAsync<BackendApiException>(() => transport.SendAsync(new BackendRequest("GET", "/v1/meetings"), default));
        Assert.Equal("unauthorized", ex.Code);
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("bad key", ex.Message);
        Assert.Equal("req-9", ex.RequestId);
    }

    [Fact]
    public async Task Send_MapsNonJsonError_ToHttpError()
    {
        var handler = new BackendScriptedHandler();
        handler.Enqueue(HttpStatusCode.BadGateway, "<html>oops</html>", "text/html");
        using var transport = new BackendHttpTransport("http://localhost:8080", "k", handler);
        var ex = await Assert.ThrowsAsync<BackendApiException>(() => transport.SendAsync(new BackendRequest("GET", "/healthz"), default));
        Assert.Equal("http_error", ex.Code);
        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task Send_Multipart_UsesFieldNameFile_AndFileBytes()
    {
        var handler = new BackendScriptedHandler();
        handler.Enqueue(HttpStatusCode.Created, "{}");
        using var transport = new BackendHttpTransport("http://localhost:8080", "k", handler);
        var file = TempFile("hello-audio");
        try
        {
            await transport.SendAsync(new BackendRequest("POST", "/v1/meetings/m1/media",
                new Dictionary<string, string> { ["kind"] = "audio", ["role"] = "primary" },
                UploadPath: file, UploadMime: "audio/mp4"), default);
        }
        finally { File.Delete(file); }
        Assert.Contains("kind=audio", handler.Requests[0].Uri);
        Assert.Contains("name=file", handler.Requests[0].Body);
        Assert.Contains("hello-audio", handler.Requests[0].Body);
    }

    [Fact]
    public void Ctor_Rejects_NonAbsoluteUrl_AndMissingKey()
    {
        Assert.Throws<ArgumentException>(() => new BackendHttpTransport("localhost:8080", "k"));
        Assert.Throws<ArgumentException>(() => new BackendHttpTransport("http://localhost:8080", " "));
    }
}
