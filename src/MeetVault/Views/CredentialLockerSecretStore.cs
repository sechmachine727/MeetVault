using MeetVault.Core;
using Windows.Security.Credentials;

namespace MeetVault.Views;

// FR8: Gemini key lives in Windows Credential Locker, never in files or logs.
public sealed class CredentialLockerSecretStore : ISecretStore
{
    private const string Resource = "MeetVault";
    private readonly PasswordVault _vault = new();

    public string? Get(string key)
    {
        try
        {
            return _vault.Retrieve(Resource, key).Password;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        try { _vault.Add(new PasswordCredential(Resource, key, value)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Already stored: replace it.
            _vault.Remove(_vault.Retrieve(Resource, key));
            _vault.Add(new PasswordCredential(Resource, key, value));
        }
    }

    public void Clear(string key)
    {
        try { _vault.Remove(_vault.Retrieve(Resource, key)); } catch { }
    }
}

