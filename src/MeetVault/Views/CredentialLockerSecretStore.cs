using MeetVault.Core;
using Windows.Security.Credentials;

namespace MeetVault.Views;

// FR8: Gemini key lives in Windows Credential Locker, never in files or logs.
public sealed class CredentialLockerSecretStore : ISecretStore
{
    private const string Resource = "MeetVault";
    // ponytail: lazy so unpackaged runs without identity construct fine; every op already swallows.
    private PasswordVault? _vault;
    private PasswordVault Vault => _vault ??= new();

    public string? Get(string key)
    {
        try
        {
            return Vault.Retrieve(Resource, key).Password;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        try { Vault.Add(new PasswordCredential(Resource, key, value)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Already stored: replace it.
            Vault.Remove(Vault.Retrieve(Resource, key));
            Vault.Add(new PasswordCredential(Resource, key, value));
        }
    }

    public void Clear(string key)
    {
        try { Vault.Remove(Vault.Retrieve(Resource, key)); } catch { }
    }
}
