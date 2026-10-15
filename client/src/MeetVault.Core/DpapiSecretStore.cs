using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MeetVault.Core;

// A secret store for machines where the WinRT PasswordVault is unavailable — no package identity
// AND the OS refusing the vault. It is NOT the everyday path: measured 2026-10-15, the Locker
// works in an unpackaged install (the freshly installed build read back a token stored by an
// earlier unpackaged run), so SecretStores.Choose normally returns the Locker and this store never
// runs. It exists so that a sign-in on a locked-down machine cannot silently fail to persist.
// Each key is one file under <root>, DPAPI-protected for the current user: never plaintext on
// disk, never a log line, and unreadable by another user or machine.
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    // Fixed entropy: it is not a secret (it ships in the binary), it only binds the blob to this
    // purpose so a stray Unprotect elsewhere cannot read it back by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeetVault.secrets.v1");

    private readonly string _root;

    public DpapiSecretStore(string root) => _root = root;

    // Keys are opaque and may hold '/', ':' or spaces: name the file after the hex of the key's
    // UTF-8 bytes so any key is a legal, unique filename.
    private string PathFor(string key)
        => Path.Combine(_root, Convert.ToHexString(Encoding.UTF8.GetBytes(key ?? "")) + ".bin");

    public string? Get(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Missing, corrupt, or written by another user: "no value", never a throw. Signing in
            // again is the recovery, and it must not crash the app on the way there.
            return null;
        }
    }

    public void Set(string key, string value)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(_root);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value ?? ""), Entropy, DataProtectionScope.CurrentUser);
        // Write beside the target, then move: a crash mid-write cannot leave a half-written secret.
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear(string key)
    {
        try { File.Delete(PathFor(key)); } catch { }
    }
}
