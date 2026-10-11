using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The pipeline switches read when the Live engines resolve. `transcription.diarization`
// is written by the Settings Performance section; off means the sherpa diarization
// engine is never constructed (each utterance then costs only the whisper pass).
public sealed class TranscriptionPipelineTests
{
    [Theory]
    [InlineData(null, true)]       // missing row: default on — a fresh install keeps diarization
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("banana", true)]   // unknown value: default on
    [InlineData("off", false)]
    [InlineData("OFF", false)]     // case-insensitive
    [InlineData(" off ", false)]   // whitespace-tolerant
    public void DiarizationEnabled_OnlyExplicitOffDisables(string? setting, bool expected)
        => Assert.Equal(expected, TranscriptionPipeline.DiarizationEnabled(setting));
}
