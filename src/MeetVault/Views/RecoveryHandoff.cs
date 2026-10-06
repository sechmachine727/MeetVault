namespace MeetVault.Views;

// Handoff for crash recovery: Dashboard stashes the meeting id, Live picks it up on load.
public static class PendingRecovery
{
    public static string? MeetingId { get; set; }
}

// Single place that knows whether the real engines can run.
public static class EngineModels
{
    public static bool Available =>
        System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "models", "ggml-base.bin")) &&
        System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "models", "sherpa-seg.onnx")) &&
        System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "models", "sherpa-emb.onnx"));
}

