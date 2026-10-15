using MeetVault.Core;
using Windows.Security.Credentials;

namespace MeetVault.Views;

// FR8: cloud credentials (the session token) live in Windows Credential Locker, never in files or logs.
public sealed class CredentialLockerSecretStore : ISecretStore
{
    private const string Resource = "MeetVault";
    // ponytail: lazy so unpackaged runs without identity construct fine; every op already swallows.
    private PasswordVault? _vault;
    // Construction itself can throw without package identity; never let it escape.
    private PasswordVault? Vault
    {
        get
        {
            if (_vault is not null) return _vault;
            try { _vault = new(); return _vault; }
            catch { return null; }
        }
    }

    // The WinRT vault needs package identity: in an unpackaged install (portable zip, classic
    // installer) this is false and SecretStores.Choose picks the DPAPI file store instead.
    public bool IsAvailable => Vault is not null;

    public string? Get(string key)
    {
        var vault = Vault;
        if (vault is null) return null;
        try
        {
            return vault.Retrieve(Resource, key).Password;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        var vault = Vault;
        if (vault is null) return;
        try { vault.Add(new PasswordCredential(Resource, key, value)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Already stored: replace it.
            vault.Remove(vault.Retrieve(Resource, key));
            vault.Add(new PasswordCredential(Resource, key, value));
        }
    }

    public void Clear(string key)
    {
        var vault = Vault;
        if (vault is null) return;
        try { vault.Remove(vault.Retrieve(Resource, key)); } catch { }
    }
}
