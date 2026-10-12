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

// Details → Backend page handoff: which meeting's backend data to open.
public static class PendingBackend
{
    public static string? MeetingId { get; set; }
}

// Single place that knows whether the configured model can run, and which model actually
// runs live (Dashboard and Live must agree on the effective choice).
public static class EngineModels
{
    private static string ModelsRoot => System.IO.Path.Combine(System.AppContext.BaseDirectory, "models");

    // The selected model's files must all exist.
    public static bool CanRun(MeetVault.Core.EngineOption option) => MeetVault.Core.EngineCatalog.IsAvailable(option, ModelsRoot);

    // The effective live resolution for the stored settings: the configured (language,
    // size, legacy) pair, a same-language/any installed substitute when it is not staged,
    // or demo when nothing can run. Both Dashboard and Live consume this, so the two
    // pages can no longer disagree about what will run.
    public sealed record LiveResolution(
        MeetVault.Core.EngineOption Requested,
        MeetVault.Core.EngineOption Option,
        bool IsFallback,
        bool Demo);

    public static LiveResolution ResolveLive(MeetVault.Core.LocalMeetingRepository repo)
    {
        // Auto mode's pre-detection default is English at the configured size, and the
        // retired legacy key must never leak a stale language into an auto meeting
        // (found live: a leftover transcription.model=small-ja resolved auto -> ja).
        var language = repo.GetSetting("transcription.language");
        var requested = string.Equals((language ?? "").Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? MeetVault.Core.EngineCatalog.Resolve("en", repo.GetSetting("transcription.size"), null)
            : MeetVault.Core.EngineCatalog.Resolve(language, repo.GetSetting("transcription.size"), repo.GetSetting("transcription.model"));
        var option = requested;
        if (!CanRun(option))
        {
            option = MeetVault.Core.EngineCatalog.Options.FirstOrDefault(o => o.Language == requested.Language && CanRun(o))
                  ?? MeetVault.Core.EngineCatalog.Options.FirstOrDefault(CanRun)
                  ?? option;
        }
        return new LiveResolution(requested, option, option != requested, !CanRun(option));
    }
}
