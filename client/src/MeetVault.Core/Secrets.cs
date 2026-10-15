namespace MeetVault.Core;

// Secret access seam. In a packaged (MSIX) install this is Windows Credential Locker; in an
// unpackaged one (portable zip, classic installer) it is a DPAPI-protected file under the app's
// data dir — see SecretStores.Choose. Either way the cloud credentials (the session token today)
// are never plaintext on disk and never in a log.
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Clear(string key);
}
