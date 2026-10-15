namespace MeetVault.Core.Backend;

// Normalisation + equality rules for the stored backend URL. Kept in Core so the Views layer
// stays a thin caller and the rules are unit-tested.
public static class BackendSettings
{
    // Legacy development address: it can only ever mean "the throwaway local server", so a
    // stored value of exactly this is migrated to the shipped default once.
    public const string LegacyLocalUrl = "http://127.0.0.1:8081";
    public const string MigratedMarkerKey = "backend.url.migrated";

    // The value a caller should actually use: the stored one when set, else the shipped default.
    public static string Resolve(string? stored)
        => string.IsNullOrWhiteSpace(stored) ? BackendDefaults.Url : Normalize(stored);

    // Trim, drop a trailing slash, drop surrounding whitespace.
    public static string Normalize(string? url) => (url ?? "").Trim().TrimEnd('/');

    // A change that must reset server-scoped state: different normalised value, both non-empty.
    public static bool IsServerChange(string? previous, string? next)
    {
        var a = Normalize(previous);
        var b = Normalize(next);
        return a.Length > 0 && b.Length > 0 && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // One-time migration of the old development URL. Returns the value to store, or null when
    // nothing should be written.
    public static string? MigrateLegacy(string? stored, string? migratedMarker)
        => string.Equals(Normalize(stored), LegacyLocalUrl, StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(migratedMarker)
               ? BackendDefaults.Url
               : null;
}
