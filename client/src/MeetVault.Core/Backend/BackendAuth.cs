using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetVault.Core.Backend;

public sealed record BackendUser(string Id, string Email, string DisplayName);

public sealed record BackendSessionInfo(string Token, string ExpiresAt, BackendUser User);

public sealed record BackendMeInfo(BackendUser User, string ExpiresAt, string IssuedAt);

// Minimal auth surface over the shared transport (bearer comes from the transport, so a
// session-scoped transport is used by all other calls; login itself uses a token-less one).
public sealed class AuthClient
{
    private readonly IBackendTransport _transport;

    public AuthClient(IBackendTransport transport) => _transport = transport;

    public async Task<BackendSessionInfo> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new LoginBody(email, password), BackendJsonContext.Default.LoginBody);
        var json = await _transport.SendAsync(new BackendRequest("POST", "/v1/auth/login", JsonBody: body), ct);
        return JsonSerializer.Deserialize(json, BackendJsonContext.Default.BackendSessionInfo)!;
    }

    public async Task<BackendMeInfo> MeAsync(CancellationToken ct = default)
    {
        var json = await _transport.SendAsync(new BackendRequest("GET", "/v1/auth/me"), ct);
        return JsonSerializer.Deserialize(json, BackendJsonContext.Default.BackendMeInfo)!;
    }

    public Task LogoutAsync(CancellationToken ct = default)
        => _transport.SendAsync(new BackendRequest("POST", "/v1/auth/logout"), ct);
}

public sealed record LoginBody(string Email, string Password);
