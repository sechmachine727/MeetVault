namespace MeetVault.Core;

/// <summary>A recordable capture target. Kind: "display" | "window".
/// Display Id = device name ("" = primary); window Id = lowercase exe name.
/// Label is the human-readable form shown in pickers.</summary>
public sealed record CaptureSource(string Kind, string Id, string Label);

/// <summary>Outcome of a recording run's finalization. Indeterminate: the stop timed out
/// while the finalize may still be completing — the file is kept, never deleted on this
/// result (review r13).</summary>
public sealed record RecordingResult(bool Ok, string? Path, long DurationMs, string? Error, bool Indeterminate = false);

/// <summary>Screen-video recorder seam. The only production implementation is
/// <see cref="ScreenRecorderLibAdapter"/>; tests fake this interface.</summary>
public interface IVideoRecorder : IDisposable
{
    bool IsRecording { get; }

    /// <summary>Starts a video-only run into <paramref name="outputPath"/> (MP4).
    /// Throws when capture cannot start at all (e.g. the native library is missing).</summary>
    Task StartAsync(CaptureSource source, string outputPath, CancellationToken ct = default);

    /// <summary>Stops the run and finalizes the file. Returns Ok=false with the
    /// reason when finalization fails or times out.</summary>
    Task<RecordingResult> StopAsync(CancellationToken ct = default);

    /// <summary>Failure during an active run (encoder error, device lost). The
    /// target window being destroyed does NOT raise this (probe P2 finding #3) —
    /// the controller watches HWND liveness itself.</summary>
    event Action<string>? Failed;

    /// <summary>Low-rate preview frames while recording: BGRA bytes, stride, width, height.
    /// Purely cosmetic — a dropped or slow consumer must never affect the recording.</summary>
    event Action<byte[], int, int, int>? FramePreview;
}
