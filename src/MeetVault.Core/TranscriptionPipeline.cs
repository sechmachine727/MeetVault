namespace MeetVault.Core;

// The pipeline switches read when the Live engines resolve. One setting, one owner:
// `transcription.diarization` is written by the Settings Performance section and read
// here by LivePage.ResolveEngines — when off, the sherpa diarization engine is never
// constructed, so each utterance costs only the whisper pass and transcription keeps
// closer to realtime. Meetings recorded without it carry no speaker identities.
public static class TranscriptionPipeline
{
    // True unless the setting is explicitly "off" (trimmed, case-insensitive).
    // null/""/unknown values keep the default (on): a missing row must not silently
    // disable diarization for existing installs.
    public static bool DiarizationEnabled(string? setting)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant();
        return s != "off";
    }
}
