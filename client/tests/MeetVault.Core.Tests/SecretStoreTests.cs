using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MeetVault.Core;

namespace MeetVault.Core.Tests;

// A machine where the WinRT PasswordVault is unavailable (no package identity AND the OS refusing
// the vault) must still be able to keep the session token: these tests pin the fallback store —
// one DPAPI-protected file per key, readable only by the same user. NOTE: this is the fallback, not
// the everyday path; the Locker itself works unpackaged (measured 2026-10-15).
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-secrets-" + Guid.NewGuid().ToString("n"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void RoundTrips_AValue()
    {
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "tok-1234567890");
        Assert.Equal("tok-1234567890", store.Get("AuthToken"));
        // The app constructs a fresh store each launch: a new instance must still see it.
        Assert.Equal("tok-1234567890", new DpapiSecretStore(_root).Get("AuthToken"));
    }

    [Fact]
    public void MissingKey_IsNull_NotAThrow()
        => Assert.Null(new DpapiSecretStore(_root).Get("Nope"));

    [Fact]
    public void Overwrite_ReplacesTheValue()
    {
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "first");
        store.Set("AuthToken", "second");
        Assert.Equal("second", store.Get("AuthToken"));
        Assert.Single(Directory.GetFiles(_root));   // one file per key, not one per write
    }

    [Fact]
    public void Clear_RemovesIt_AndIsIdempotent()
    {
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "tok");
        store.Clear("AuthToken");
        Assert.Null(store.Get("AuthToken"));
        Assert.Empty(Directory.GetFiles(_root));
        store.Clear("AuthToken");
        store.Clear("NeverExisted");
        Assert.Null(store.Get("AuthToken"));
    }

    [Fact]
    public void TheStoredFile_IsNotPlaintext()
    {
        // The whole point of the change: the token must not be readable off the disk.
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "SuperSecretTokenValue");
        var path = Directory.GetFiles(_root).Single();
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("SuperSecret", text);
        Assert.DoesNotContain("TokenValue", text);
        // ...and it is genuinely DPAPI output: the wrong entropy cannot decrypt it.
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(
            bytes, Encoding.UTF8.GetBytes("wrong-entropy"), DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void OddKeys_DoNotCollide()
    {
        // Keys are opaque: a path separator or a colon must not become a directory or a bad name.
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "a");
        store.Set("Other/Key:With Odd*Chars", "b");
        Assert.Equal("a", store.Get("AuthToken"));
        Assert.Equal("b", store.Get("Other/Key:With Odd*Chars"));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void CorruptFile_ReadsAsNull_NotAThrow()
    {
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "tok");
        File.WriteAllBytes(Directory.GetFiles(_root).Single(), new byte[] { 1, 2, 3, 4, 5 });
        Assert.Null(store.Get("AuthToken"));
    }

    [Fact]
    public void EmptyValue_RoundTrips()
    {
        // Sign-out writes an empty token in some paths; it must not crash or read back as missing.
        var store = new DpapiSecretStore(_root);
        store.Set("AuthToken", "");
        Assert.Equal("", store.Get("AuthToken"));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SecretStoreChoiceTests
{
    private sealed class Fake : ISecretStore
    {
        public string? Value;
        public string? Get(string key) => Value;
        public void Set(string key, string value) => Value = value;
        public void Clear(string key) => Value = null;
    }

    [Fact]
    public void UsesTheLocker_WhenTheAppHasIdentity()
    {
        var locker = new Fake();
        Assert.Same(locker, SecretStores.Choose(() => locker, () => new Fake()));
    }

    [Fact]
    public void FallsBack_WhenTheLockerIsUnavailable()
    {
        var fallback = new Fake();
        Assert.Same(fallback, SecretStores.Choose(() => null, () => fallback));
    }

    [Fact]
    public void FallsBack_WhenTheLockerThrows()
    {
        // WinRT can throw instead of returning null on a locked-down machine.
        var fallback = new Fake();
        Assert.Same(fallback, SecretStores.Choose(() => throw new InvalidOperationException("no identity"), () => fallback));
    }

    [Fact]
    public void FallsBack_WhenTheLockerFactoryItselfThrows()
    {
        var fallback = new Fake();
        Func<ISecretStore?> boom = () => throw new PlatformNotSupportedException();
        Assert.Same(fallback, SecretStores.Choose(boom, () => fallback));
    }
}
