using System.Runtime.CompilerServices;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

// Auth-surface fake: answers by request path from a scripted table and records the last request.
// Error entries throw the same BackendApiException the real transport produces.
internal sealed class ScriptedTransport : IBackendTransport
{
    private readonly Dictionary<string, (string Body, int Status, string? Code)> _scripted = new();
    public string LastPath { get; private set; } = "";
    public string LastBody { get; private set; } = "";
    public string LastMethod { get; private set; } = "";

    public void Respond(string path, string body, int status = 200)
        => _scripted[path] = (body, status, null);

    public void RespondError(string path, int status, string code, string body)
        => _scripted[path] = (body, status, code);

    public Task<string> SendAsync(BackendRequest request, CancellationToken ct)
    {
        LastPath = request.Path;
        LastBody = request.JsonBody ?? "";
        LastMethod = request.Method;
        if (!_scripted.TryGetValue(request.Path, out var entry))
            throw new BackendApiException(404, "not_found", "no scripted response for " + request.Path, null);
        if (entry.Status is >= 200 and < 300) return Task.FromResult(entry.Body);
        throw new BackendApiException(entry.Status, entry.Code ?? "http_error", entry.Body, null);
    }

    public Task DownloadToFileAsync(string path, string destinationPath, CancellationToken ct)
        => Task.CompletedTask;

    public async IAsyncEnumerable<string> StreamLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose() { }
}

public sealed class BackendAuthTests
{
    [Fact]
    public async Task Login_ParsesSession()
    {
        var fake = new ScriptedTransport();
        fake.Respond("/v1/auth/login",
            "{\"token\":\"tok-abc\",\"expires_at\":\"2026-11-13T04:00:00+00:00\",\"user\":{\"id\":\"u1\",\"email\":\"a@b.com\",\"display_name\":\"Ann\"}}");
        var session = await new AuthClient(fake).LoginAsync("a@b.com", "pw", default);
        Assert.Equal("tok-abc", session.Token);
        Assert.Equal("a@b.com", session.User.Email);
        Assert.Contains("/v1/auth/login", fake.LastPath);
        Assert.Contains("\"email\":\"a@b.com\"", fake.LastBody);
    }

    [Fact]
    public async Task Login_WrongPassword_SurfacesApiError()
    {
        var fake = new ScriptedTransport();
        fake.RespondError("/v1/auth/login", 401, "invalid_credentials",
            "{\"error\":{\"code\":\"invalid_credentials\",\"message\":\"Email or password is incorrect.\"},\"request_id\":\"r\"}");
        var ex = await Assert.ThrowsAsync<BackendApiException>(
            () => new AuthClient(fake).LoginAsync("a@b.com", "bad", default));
        Assert.Equal("invalid_credentials", ex.Code);
    }

    [Fact]
    public async Task Me_ParsesUser()
    {
        var fake = new ScriptedTransport();
        fake.Respond("/v1/auth/me", "{\"user\":{\"id\":\"u1\",\"email\":\"a@b.com\",\"display_name\":\"Ann\"},\"expires_at\":\"2026-11-13T04:00:00+00:00\",\"issued_at\":\"2026-10-14T04:00:00+00:00\"}");
        var me = await new AuthClient(fake).MeAsync(default);
        Assert.Equal("Ann", me.User.DisplayName);
    }

    [Fact]
    public async Task Logout_Posts()
    {
        var fake = new ScriptedTransport();
        fake.Respond("/v1/auth/logout", "{\"ok\":true}");
        await new AuthClient(fake).LogoutAsync(default);
        Assert.Equal("POST", fake.LastMethod);
    }
}
