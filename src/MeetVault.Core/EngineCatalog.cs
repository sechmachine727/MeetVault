namespace MeetVault.Core;

// One selectable speech-recognition engine + model. Labels are intentionally English
// (engine identifiers, not UI copy — same intent comment as the old model picker).
// RequiredFiles are relative to the models root; ALL must exist for the engine to run.
public sealed record EngineOption(
    string Engine,
    string Model,
    string Label,
    bool Streaming,
    string Language,
    string[] RequiredFiles);

public static class EngineCatalog
{
    public static IReadOnlyList<EngineOption> Options { get; } = new[]
    {
        new EngineOption("whisper", "tiny", "Whisper tiny (fast, multilingual)", false, "auto",
            new[] { "ggml-tiny.bin" }),
        new EngineOption("whisper", "base", "Whisper base (accurate, multilingual)", false, "auto",
            new[] { "ggml-base.bin" }),
        new EngineOption("moonshine", "tiny-en", "Moonshine tiny (English, lowest latency)", false, "en",
            new[] { "moonshine/tiny-en/encoder_model.ort", "moonshine/tiny-en/decoder_model_merged.ort", "moonshine/tiny-en/tokens.txt" }),
        new EngineOption("moonshine", "tiny-ja", "Moonshine tiny (Japanese, lowest latency)", false, "ja",
            new[] { "moonshine/tiny-ja/encoder_model.ort", "moonshine/tiny-ja/decoder_model_merged.ort", "moonshine/tiny-ja/tokens.txt" }),
        new EngineOption("moonshine", "base-en", "Moonshine base (English, balanced)", false, "en",
            new[] { "moonshine/base-en/encoder_model.ort", "moonshine/base-en/decoder_model_merged.ort", "moonshine/base-en/tokens.txt" }),
        new EngineOption("moonshine", "base-ja", "Moonshine base (Japanese, balanced)", false, "ja",
            new[] { "moonshine/base-ja/encoder_model.ort", "moonshine/base-ja/decoder_model_merged.ort", "moonshine/base-ja/tokens.txt" }),
        new EngineOption("zipformer", "20m-en", "Zipformer 20M int8 (English, streaming)", true, "en",
            new[] { "zipformer/20m-en/encoder-epoch-99-avg-1.int8.onnx", "zipformer/20m-en/decoder-epoch-99-avg-1.int8.onnx", "zipformer/20m-en/joiner-epoch-99-avg-1.int8.onnx", "zipformer/20m-en/tokens.txt" }),
        // The staged Vosk small models ship the dynamic-graph parts (Gr.fst + HCLr.fst), NOT a
        // pre-composed HCLG.fst — the native composes the decode graph at load time.
        new EngineOption("vosk", "small-en", "Vosk small (English, streaming)", true, "en",
            new[] { "vosk/small-en/am/final.mdl", "vosk/small-en/conf/model.conf", "vosk/small-en/graph/HCLr.fst", "vosk/small-en/graph/Gr.fst", "vosk/small-en/ivector/final.ie" }),
        new EngineOption("vosk", "small-ja", "Vosk small (Japanese, streaming)", true, "ja",
            new[] { "vosk/small-ja/am/final.mdl", "vosk/small-ja/conf/model.conf", "vosk/small-ja/graph/HCLr.fst", "vosk/small-ja/graph/Gr.fst", "vosk/small-ja/ivector/final.ie" }),
    };

    public static EngineOption Default => Options[0];

    // Missing/unknown engine or model falls back to the default (whisper tiny): an old
    // install with only `transcription.model=base` keeps working with no migration.
    public static EngineOption Resolve(string? engine, string? model)
    {
        var e = (engine ?? "").Trim().ToLowerInvariant();
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) e = "whisper";
        foreach (var o in Options)
            if (o.Engine == e && o.Model == m) return o;
        foreach (var o in Options)
            if (o.Engine == e) return o;
        return Default;
    }

    public static bool IsAvailable(EngineOption option, string modelsRoot)
        => option.RequiredFiles.All(f => File.Exists(Path.Combine(modelsRoot, f)));

    public static int IndexOf(string? engine, string? model)
    {
        var resolved = Resolve(engine, model);
        for (var i = 0; i < Options.Count; i++)
            if (Options[i] == resolved) return i;
        return 0;
    }
}
