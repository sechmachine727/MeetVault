using MeetVault.Core.Backend;

namespace MeetVault.Views;

// Owns the backend sign-in state for the whole app. The session token lives in the Windows
// Credential Locker (never in the DB); the email lives in the settings store for display.
// Cloud surfaces consult IsSignedIn; local recording/transcription never require it.
public static class AppSession
{
    private const string TokenKey = "AuthToken";

    public static BackendUser? User { get; private set; }
    public static string? ExpiresAt { get; private set; }
    public static string? LastError { get; private set; }

    public static bool IsSignedIn => User is not null;

    public static string Url => BackendSettings.Resolve(AppStore.Repo.GetSetting("backend.url"));

    // A client with the stored session token. Callers pattern:
    //   if (!AppSession.TryCreateClient(out var client)) { show sign-in prompt; return; }
    public static bool TryCreateClient(out BackendClient client)
    {
        client = null!;
        var token = AppStore.Secrets.Get(TokenKey);
        if (Url.Length == 0 || token is not { Length: > 0 }) return false;
        client = new BackendClient(new BackendHttpTransport(Url, token));
        return true;
    }

    public static async Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        if (Url.Length == 0) throw new InvalidOperationException("no-url");
        using var transport = new BackendHttpTransport(Url, null);
        var session = await new AuthClient(transport).LoginAsync(email.Trim(), password, ct);
        AppStore.Secrets.Set(TokenKey, session.Token);
        AppStore.Repo.SetSetting("backend.email", session.User.Email);
        AppStore.Repo.SetSetting("backend.display_name", session.User.DisplayName);
        ClearError();
        User = session.User;
        ExpiresAt = session.ExpiresAt;
    }

    public static async Task SignOutAsync()
    {
        try
        {
            var token = AppStore.Secrets.Get(TokenKey);
            if (token is { Length: > 0 } && Url.Length > 0)
            {
                using var transport = new BackendHttpTransport(Url, token);
                await new AuthClient(transport).LogoutAsync(); // best effort; local state clears regardless
            }
        }
        catch { }
        Clear();
    }

    // Startup / page-entry validation. A 401 removes the stored token (expired or revoked);
    // a network failure keeps the token (it may still be valid) and reports LastError.
    public static async Task<bool> RestoreAsync()
    {
        ClearError();
        var token = AppStore.Secrets.Get(TokenKey);
        if (Url.Length == 0 || token is not { Length: > 0 }) { Clear(); return false; }
        try
        {
            using var transport = new BackendHttpTransport(Url, token);
            var me = await new AuthClient(transport).MeAsync();
            User = me.User;
            ExpiresAt = me.ExpiresAt;
            return true;
        }
        catch (BackendApiException ex) when (ex.StatusCode == 401)
        {
            LastError = Loc.T("Acc.SessionExpired");
            Clear();
            return false;
        }
        catch
        {
            User = null;
            LastError = Loc.T("Acc.Unreachable");
            return false;
        }
    }

    // Any cloud call that returns 401 funnels here; the UI then shows AppSession.LastError.
    public static void NoteAuthFailure(BackendApiException ex)
    {
        if (ex.StatusCode != 401) return;
        LastError = Loc.T("Acc.SessionExpired");
        Clear();
    }

    public static void ClearError() => LastError = null;

    // Drop the in-memory sign-in state without calling the server (used when the server changes:
    // the stored token belongs to the previous server and must not be reused).
    public static void ClearSignInState()
    {
        User = null;
        ExpiresAt = null;
        LastError = null;
    }

    private static void Clear()
    {
        AppStore.Secrets.Clear(TokenKey);
        User = null;
        ExpiresAt = null;
    }
}
