namespace MeetVault.Core;

// One selectable speech model row — a concrete staged artifact. MeetVault ships the Vosk
// family only (offline, streaming, CPU): each row is one (language, size tier) pair of the
// model tree. Label is intentionally English (an engine identifier, not UI copy).
// RequiredFiles are relative to the models root; ALL must exist for the model to run.
public sealed record EngineOption(
    string Language,
    string Size,
    string Model,
    string Label,
    int DownloadMb,
    string[] RequiredFiles);

public static class EngineCatalog
{
    // Display order of the model-size dropdown (lightest first).
    public static readonly string[] Tiers = { "small", "medium", "large" };

    // Language dropdown: endonyms (standard convention); Code is the persisted value.
    public static readonly (string Code, string Endonym)[] Languages =
    {
        ("en", "English"), ("ja", "日本語"), ("vi", "Tiếng Việt"),
    };

    // Dynamic-graph models ship Gr.fst + HCLr.fst and the native composes the decode graph
    // at load time; composed models ship the pre-built HCLG.fst instead.
    private static string[] Parts(string model, bool composed = false)
    {
        var graph = composed
            ? new[] { "graph/HCLG.fst" }
            : new[] { "graph/HCLr.fst", "graph/Gr.fst" };
        return new[]
        {
            "vosk/" + model + "/am/final.mdl",
            "vosk/" + model + "/conf/model.conf",
        }.Concat(graph).Concat(new[] { "vosk/" + model + "/ivector/final.ie" }).ToArray();
    }

    public static IReadOnlyList<EngineOption> Options { get; } = new[]
    {
        new EngineOption("en", "small", "small-en", "Vosk small — English", 40, Parts("small-en")),
        new EngineOption("en", "medium", "medium-en", "Vosk medium — English", 128, Parts("medium-en")),
        new EngineOption("en", "large", "large-en", "Vosk large — English", 1800, Parts("large-en", composed: true)),
        new EngineOption("ja", "small", "small-ja", "Vosk small — Japanese", 48, Parts("small-ja")),
        new EngineOption("ja", "large", "large-ja", "Vosk large — Japanese", 1000, Parts("large-ja", composed: true)),
        new EngineOption("vi", "small", "small-vi", "Vosk small — Vietnamese", 32, Parts("small-vi")),
        new EngineOption("vi", "large", "large-vi", "Vosk large — Vietnamese", 78, Parts("large-vi", composed: true)),
    };

    public static EngineOption Default => Find("en", "small")!;

    public static EngineOption? Find(string? language, string? size)
    {
        var l = (language ?? "").Trim().ToLowerInvariant();
        var s = (size ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Language == l && o.Size == s) return o;
        return null;
    }

    // Retired single-key store (`transcription.model`) -> (language, size). Anything
    // unknown (older removed engines: tiny, base, 20m-en, …) maps to the default pair.
    public static (string Language, string Size) MigrateLegacy(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Model == m) return (o.Language, o.Size);
        return ("en", "small");
    }

    // Exact (language, size) wins; then the same language's small tier (requested tier has
    // no artifact or no value); then the retired single key; then the default.
    public static EngineOption Resolve(string? language, string? size, string? legacyModel = null)
    {
        var exact = Find(language, size);
        if (exact is not null) return exact;
        var l = (language ?? "").Trim().ToLowerInvariant();
        if (l.Length > 0)
        {
            var sameSmall = Find(l, "small");
            if (sameSmall is not null) return sameSmall;
        }
        var (ml, ms) = MigrateLegacy(legacyModel);
        return Find(ml, ms) ?? Default;
    }

    public static bool IsAvailable(EngineOption option, string modelsRoot)
        => option.RequiredFiles.All(f => File.Exists(Path.Combine(modelsRoot, f)));
}
