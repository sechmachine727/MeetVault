namespace MeetVault.Core;

public static class SecretStores
{
    // Prefer the Windows Credential Locker — it is the product requirement (FR8) and an MSIX
    // install has the package identity it needs. Fall back to the DPAPI file store for unpackaged
    // installs (the portable zip, a classic installer), where the locker is unavailable: WinRT can
    // return null OR throw there, so both outcomes fall through.
    public static ISecretStore Choose(Func<ISecretStore?> locker, Func<ISecretStore> fallback)
    {
        try
        {
            var store = locker();
            if (store is not null) return store;
        }
        catch
        {
            // No identity, or WinRT refusing: use the fallback.
        }
        return fallback();
    }
}
