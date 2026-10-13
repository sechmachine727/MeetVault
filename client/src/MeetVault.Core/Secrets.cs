namespace MeetVault.Core;

// Secret access seam (Windows Credential Locker in the app, a fake in tests). The cloud
// credentials — the session token today — are stored here, never in a file or a log.
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Clear(string key);
}
