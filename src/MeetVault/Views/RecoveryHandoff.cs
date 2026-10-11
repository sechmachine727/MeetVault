namespace MeetVault.Views;

// Handoff for crash recovery: Dashboard stashes the meeting id, Live picks it up on load.
public static class PendingRecovery
{
    public static string? MeetingId { get; set; }
}

// Dashboard/Meetings set this before navigating so Details opens the clicked meeting.
public static class PendingDetails
{
    public static string? MeetingId { get; set; }
}

// Single place that knows whether the configured engine can run.
public static class EngineModels
{
    private static string ModelsRoot => System.IO.Path.Combine(System.AppContext.BaseDirectory, "models");

    // The selected engine's model files must all exist.
    public static bool CanRun(MeetVault.Core.EngineOption option) => MeetVault.Core.EngineCatalog.IsAvailable(option, ModelsRoot);

    // The sherpa diarizer pair; absent => single-speaker mode (with a warn), not demo.
    public static bool DiarizerAvailable =>
        System.IO.File.Exists(System.IO.Path.Combine(ModelsRoot, "sherpa-seg.onnx"))
        && System.IO.File.Exists(System.IO.Path.Combine(ModelsRoot, "sherpa-emb.onnx"));

    public static string? WhisperModel
    {
        get
        {
            var dir = ModelsRoot;
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-tiny.bin"))) return "ggml-tiny.bin";
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-base.bin"))) return "ggml-base.bin";
            return null;
        }
    }
}
