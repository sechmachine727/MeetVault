namespace MeetVault.Core;

public static class SecretStores
{
    // Windows Credential Locker is the product requirement (FR8), and it works in practice for a
    // packaged (MSIX) install and for an unpackaged one. Fall back to the DPAPI file store when the
    // Locker is not available at all — WinRT can return null OR throw there — so that a sign-in
    // never silently fails to persist on a locked-down machine.
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
