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
    // The deliverable ships exactly two models — see docs/ASR-ENGINES.md. English and the
    // small/medium tiers were dropped so the portable zip stays shippable (~1.5 GB of models
    // instead of 4.1 GB); there is no runtime download path to fetch the rest.
    // Keep this list, docs/ASR-ENGINES.md and tools/pack-portable.ps1 in step.

    // Display order of the model-size dropdown.
    public static readonly string[] Tiers = { "large" };

    // Language dropdown: endonyms (standard convention); Code is the persisted value.
    public static readonly (string Code, string Endonym)[] Languages =
    {
        ("ja", "日本語"), ("vi", "Tiếng Việt"),
    };

    // Dynamic-graph models ship Gr.fst + HCLr.fst and the native composes the decode graph
    // at load time; composed models ship the pre-built HCLG.fst instead.
    private static string[] Parts(string model, bool composed = false)
    {
        var graph = composed
            ? new[] { "vosk/" + model + "/graph/HCLG.fst" }
            : new[] { "vosk/" + model + "/graph/HCLr.fst", "vosk/" + model + "/graph/Gr.fst" };
        return new[]
        {
            "vosk/" + model + "/am/final.mdl",
            "vosk/" + model + "/conf/model.conf",
        }.Concat(graph).Concat(new[] { "vosk/" + model + "/ivector/final.ie" }).ToArray();
    }

    public static IReadOnlyList<EngineOption> Options { get; } = new[]
    {
        new EngineOption("ja", "large", "large-ja", "Vosk large — Japanese", 1000, Parts("large-ja", composed: true)),
        new EngineOption("vi", "large", "large-vi", "Vosk large — Vietnamese", 78, Parts("large-vi", composed: true)),
    };

    // A fresh install starts on the primary shipped language; the other is one click away.
    public static EngineOption Default => Options[0];

    public static EngineOption? Find(string? language, string? size)
    {
        var l = (language ?? "").Trim().ToLowerInvariant();
        var s = (size ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Language == l && o.Size == s) return o;
        return null;
    }

    // Retired single-key store (`transcription.model`) -> (language, size). Anything unknown
    // (older removed engines: tiny, base, small-en, medium-en, …) maps to the shipped default.
    public static (string Language, string Size) MigrateLegacy(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Model == m) return (o.Language, o.Size);
        return (Default.Language, Default.Size);
    }

    // Exact (language, size) wins; then any tier of that language; then the retired single key;
    // then the shipped default. A stored language that no longer ships (e.g. English) lands on
    // the default rather than on a model that is not on disk.
    public static EngineOption Resolve(string? language, string? size, string? legacyModel = null)
    {
        var exact = Find(language, size);
        if (exact is not null) return exact;
        var l = (language ?? "").Trim().ToLowerInvariant();
        if (l.Length > 0)
        {
            foreach (var o in Options)
                if (o.Language == l) return o;
        }
        var (ml, ms) = MigrateLegacy(legacyModel);
        return Find(ml, ms) ?? Default;
    }

    public static bool IsAvailable(EngineOption option, string modelsRoot)
        => option.RequiredFiles.All(f => File.Exists(Path.Combine(modelsRoot, f)));
}
