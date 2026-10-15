namespace MeetVault.Core;

// Secret access seam. Windows Credential Locker in practice — for an MSIX install and, measured
// 2026-10-15, for an unpackaged one too (a token stored by an unpackaged run was read back by a
// freshly installed build). When the Locker is unavailable (no identity AND the OS refusing the
// vault) the DPAPI file store under the app's data dir takes over — see SecretStores.Choose.
// Either way the cloud credentials (the session token today) are never plaintext and never logged.
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Clear(string key);
}
