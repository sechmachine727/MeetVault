namespace MeetVault.Views;

// Handoff for crash recovery: Dashboard stashes the meeting id, Live picks it up on load.
public static class PendingRecovery
{
    public static string? MeetingId { get; set; }
}

// Single place that knows whether the real engines can run.
public static class EngineModels
{
    // Either whisper model unlocks live; base is reserved for refinement.
    public static bool Available => WhisperModel is not null
        && System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "models", "sherpa-seg.onnx"))
        && System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "models", "sherpa-emb.onnx"));

    public static string? WhisperModel
    {
        get
        {
            var dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "models");
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-tiny.bin"))) return "ggml-tiny.bin";
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-base.bin"))) return "ggml-base.bin";
            return null;
        }
    }
}
