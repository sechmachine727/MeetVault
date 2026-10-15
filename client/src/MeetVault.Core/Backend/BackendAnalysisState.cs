namespace MeetVault.Core.Backend;

public enum AnalysisState { NotAnalyzed, Uploading, Queued, Processing, Completed, Failed }

// What the next Analyze click must do for a meeting whose sync row exists.
// Fired when a backend id is on file but the SERVER no longer has that meeting: a deleted cloud
// copy (the backend soft-deletes and keeps the row, so only the client's link dangles) cannot be
// analyzed — it has to be re-created and re-uploaded, and the server's create is idempotent on
// (source, external_id), so the re-send replaces the tombstone instead of duplicating it.
public enum BackendAction { Send, Rerun }

// Single mapping from backend-reported state to the six user-visible analysis states.
// Rules (keep in sync with docs/INTEGRATION.md):
// - Backend status is authoritative whenever it is available (status != null).
// - Uploading is a client-only state (media is still being sent, backend unknown/not started).
// - Completed requires the analyze step succeeded AND fresh analysis documents exist.
public static class BackendAnalysisState
{
    public static AnalysisState Map(BackendSyncRow? sync, BackendStatus? status, bool hasFreshAnalysis)
    {
        if (status is not null)
        {
            status.Steps.TryGetValue("analyze", out var analyze);
            if (analyze == "failed" || status.Status == "failed") return AnalysisState.Failed;
            if (analyze == "succeeded" && hasFreshAnalysis) return AnalysisState.Completed;
            if (status.Steps.Values.Any(v => v == "running")) return AnalysisState.Processing;
            if (status.Steps.Values.Any(v => v == "queued")) return AnalysisState.Queued;
            return AnalysisState.NotAnalyzed;
        }
        return sync?.State switch
        {
            "sending" => AnalysisState.Uploading,
            "processing" => AnalysisState.Processing,
            "ready" => AnalysisState.Completed,
            // A dangling link (the server deleted the meeting) is not an analysis failure: it is
            // back to "not on the backend yet", which is what the user can act on by sending again.
            "not_found" => AnalysisState.NotAnalyzed,
            "failed" => AnalysisState.Failed,
            _ => AnalysisState.NotAnalyzed,
        };
    }

    // Send vs re-run for a meeting that already has a backend link. "not_found" means the server
    // holds no such meeting, so the only way forward is a full send (create + media + transcript);
    // a re-run against the dead id can only answer 404.
    public static BackendAction NextAction(BackendSyncRow? sync)
        => !string.IsNullOrEmpty(sync?.BackendId) && sync!.State != "not_found"
            ? BackendAction.Rerun
            : BackendAction.Send;

    public static AnalysisState FromCloudRow(string meetingStatus, string? latestJobType, string? latestJobState, bool hasAnalysis)
    {
        if (latestJobType == "analyze" && latestJobState == "succeeded" && hasAnalysis) return AnalysisState.Completed;
        if (latestJobState == "running") return AnalysisState.Processing;
        if (latestJobState == "queued") return AnalysisState.Queued;
        if (latestJobState == "failed" || meetingStatus == "failed") return AnalysisState.Failed;
        return meetingStatus == "ready" ? AnalysisState.Completed : AnalysisState.NotAnalyzed;
    }

    // The mock badge reflects the ANALYSIS result, never the server-side pipeline flags:
    // a real analyzer run over a mock server-side transcript is a real analysis. The run's
    // is_mock is authoritative; the bundle-level mock is the fallback when the run record
    // is absent; no analysis loaded means no badge.
    public static bool ShowMockBadge(BackendBundle? analysis) =>
        analysis is not null && (analysis.Run?.IsMock ?? analysis.Mock);
}
