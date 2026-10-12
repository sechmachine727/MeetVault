using System.Globalization;

namespace MeetVault.Core.Backend;

public enum BackendSyncStage { Creating, UploadingAudio, UploadingVideo, UploadingTranscript, Starting }

public sealed record BackendSyncResult(string BackendId, int TranscriptVersion, BackendProcessResult Process);

// Orchestrates one "send this meeting to the backend" run.
// Media is uploaded only on the FIRST send: POST .../media appends an asset every call, so a
// later send uploads a NEW transcript version and re-runs only the analysis (force=true).
public sealed class BackendSync
{
    private readonly BackendClient _client;

    public BackendSync(BackendClient client) => _client = client;

    public async Task<BackendSyncResult> SendAsync(
        Meeting meeting,
        IReadOnlyList<Segment> segments,
        IReadOnlyList<Speaker> speakers,
        IReadOnlyList<MediaTrack> screenTracks,
        string? existingBackendId,
        IProgress<BackendSyncStage>? progress = null,
        CancellationToken ct = default)
    {
        var firstSend = string.IsNullOrEmpty(existingBackendId);
        var backendId = existingBackendId;
        long? probedMs = null;
        if (firstSend)
        {
            progress?.Report(BackendSyncStage.Creating);
            var created = await _client.CreateMeetingAsync(meeting, ct);
            backendId = created.Id;
            AppLog.Info("backend: created meeting=" + backendId + " external=" + meeting.Id);
            if (meeting.MediaPath is { } audio && File.Exists(audio))
            {
                progress?.Report(BackendSyncStage.UploadingAudio);
                var upload = await _client.UploadMediaAsync(backendId!, audio, meeting.MediaMime ?? "audio/mp4", "audio", 0, ct);
                probedMs = upload.DurationMs;
                AppLog.Info("backend: audio uploaded asset=" + upload.AssetId + " duration_ms=" + upload.DurationMs);
            }
            foreach (var track in screenTracks.OrderBy(t => t.StartOffsetMs).ThenBy(t => t.CreatedAt, StringComparer.Ordinal))
            {
                if (!File.Exists(track.Path)) continue;
                progress?.Report(BackendSyncStage.UploadingVideo);
                var upload = await _client.UploadMediaAsync(backendId!, track.Path, track.Mime, "video", track.StartOffsetMs, ct);
                AppLog.Info("backend: video uploaded asset=" + upload.AssetId + " offset_ms=" + track.StartOffsetMs);
            }
        }
        else
        {
            // Re-send: media was uploaded on the first send; recover its probed duration so the
            // formatter can pin the final timestamp against the server's align=auto scaling.
            probedMs = (await _client.ListMediaAsync(backendId!, ct)).FirstOrDefault(m => m.Kind == "audio")?.DurationMs;
        }
        // The server rejects segments: [] (400 unsupported_transcript_format), which would fail an
        // otherwise valid media-only send — so an empty transcript is skipped, not uploaded.
        var transcriptVersion = 0;
        if (segments.Count > 0)
        {
            progress?.Report(BackendSyncStage.UploadingTranscript);
            var formatA = BackendFormatter.ToFormatA(meeting, segments, speakers, probedMs);
            var transcript = await _client.UploadTranscriptAsync(backendId!, formatA, meeting.Language, ct);
            AppLog.Info("backend: transcript uploaded version=" + transcript.Version);
            transcriptVersion = transcript.Version;
        }
        else
        {
            AppLog.Info("backend: transcript skipped (no segments)");
        }
        progress?.Report(BackendSyncStage.Starting);
        var steps = firstSend
            ? new List<string> { "transcribe", "diarize", "align", "analyze" }
            : new List<string> { "analyze" };
        var result = await _client.ProcessAsync(backendId!, steps, force: !firstSend, ct: ct);
        AppLog.Info($"backend: process enqueued={result.Enqueued} steps={string.Join(",", result.Steps)}");
        return new BackendSyncResult(backendId!, transcriptVersion, result);
    }

    // Polls /status every 2 s until a terminal state (or cancellation); returns the last status.
    public async Task<BackendStatus> WaitForTerminalAsync(string backendId, Action<BackendStatus>? onTick = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken ct = default)
    {
        var wait = delay ?? Task.Delay;
        while (true)
        {
            var status = await _client.GetStatusAsync(backendId, ct);
            AppLog.Info("backend: status " + status.Status + " progress=" + status.Progress.ToString("0.00", CultureInfo.InvariantCulture));
            onTick?.Invoke(status);
            if (status.Status is "ready" or "failed" or "deleted") return status;
            await wait(TimeSpan.FromSeconds(2), ct);
        }
    }
}
