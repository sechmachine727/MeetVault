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

// Single place that knows whether the configured model can run.
public static class EngineModels
{
    private static string ModelsRoot => System.IO.Path.Combine(System.AppContext.BaseDirectory, "models");

    // The selected model's files must all exist.
    public static bool CanRun(MeetVault.Core.EngineOption option) => MeetVault.Core.EngineCatalog.IsAvailable(option, ModelsRoot);
}
