using System.Net;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public class BackendDefaultsTests
{
    [Fact]
    public void DefaultUrl_IsAbsoluteHttps_WithoutTrailingSlash()
    {
        Assert.True(Uri.TryCreate(BackendDefaults.Url, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.False(BackendDefaults.Url.EndsWith('/'));
    }

    [Fact]
    public async Task DefaultUrl_IsAcceptedByTheTransport_AndBuildsTheExpectedUri()
    {
        var handler = new BackendScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        using var transport = new BackendHttpTransport(BackendDefaults.Url, null, handler);
        await transport.SendAsync(new BackendRequest("GET", "/healthz"), default);
        Assert.Equal("https://meetvault.supersanta.io.vn/healthz", handler.Requests[0].Uri);
    }
}
