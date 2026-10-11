namespace MeetVault.Core;

// One selectable speech model. MeetVault ships the Vosk small family only (offline,
// streaming, CPU) — a single engine family keeps the pipeline, the settings and the
// model tree simple. Label is intentionally English (an engine identifier, not UI copy).
// RequiredFiles are relative to the models root; ALL must exist for the model to run.
public sealed record EngineOption(
    string Model,
    string Label,
    string Language,
    string[] RequiredFiles);

public static class EngineCatalog
{
    // The staged Vosk small models ship the dynamic-graph parts (Gr.fst + HCLr.fst), NOT a
    // pre-composed HCLG.fst — the native composes the decode graph at load time.
    private static string[] Parts(string model) => new[]
    {
        "vosk/" + model + "/am/final.mdl",
        "vosk/" + model + "/conf/model.conf",
        "vosk/" + model + "/graph/HCLr.fst",
        "vosk/" + model + "/graph/Gr.fst",
        "vosk/" + model + "/ivector/final.ie",
    };

    public static IReadOnlyList<EngineOption> Options { get; } = new[]
    {
        new EngineOption("small-en", "Vosk small — English", "en", Parts("small-en")),
        new EngineOption("small-ja", "Vosk small — Japanese", "ja", Parts("small-ja")),
        new EngineOption("small-vi", "Vosk small — Vietnamese", "vi", Parts("small-vi")),
    };

    public static EngineOption Default => Options[0]; // Vosk small — English

    // Missing/unknown model falls back to the default (English): a legacy install whose
    // transcription.model still names a removed engine (tiny, base, tiny-en, 20m-en, …)
    // keeps working with no migration.
    public static EngineOption Resolve(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Model == m) return o;
        return Default;
    }

    public static bool IsAvailable(EngineOption option, string modelsRoot)
        => option.RequiredFiles.All(f => File.Exists(Path.Combine(modelsRoot, f)));

    public static int IndexOf(string? model)
    {
        var resolved = Resolve(model);
        for (var i = 0; i < Options.Count; i++)
            if (Options[i] == resolved) return i;
        return 0;
    }
}
