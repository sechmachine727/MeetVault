using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Skips silently when the (gitignored) moonshine models are absent — a missing payload
// is an environment state, never a test failure.
public sealed class SherpaOfflineTranscriberTests
{
    private static string? ModelDir()
    {
        var dir = Path.Combine(TestAudio.RepoRoot, "models", "moonshine", "tiny-en");
        return File.Exists(Path.Combine(dir, "encoder_model.ort")) ? dir : null;
    }

    [Fact]
    public async Task DecodesTheFixture()
    {
        var dir = ModelDir();
        if (dir is null) return;
        var wav = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        var pcm = Pcm16.FromFloats(TestAudio.ReadWav16k(wav));
        using var tx = new SherpaOfflineTranscriber(dir);
        var segs = new List<Segment>();
        tx.SegmentReady += segs.Add;
        await tx.StartAsync(CancellationToken.None);
        await tx.TranscribeWindowAsync(pcm, 0, "test-meeting");
        // 17.4 s input -> the engine chunks it; every emitted segment must sit inside the clip.
        Assert.NotEmpty(segs);
        Assert.Contains(segs, s => s.Text.Contains("fox", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, segs[0].StartMs);
        for (var i = 1; i < segs.Count; i++) Assert.True(segs[i].StartMs >= segs[i - 1].StartMs);
        Assert.All(segs, s => Assert.True(s.EndMs <= pcm.Length / 32)); // 32 bytes per ms at 16 kHz
    }

    [Fact]
    public async Task EmptyAudio_NoSegments_NoThrow()
    {
        var dir = ModelDir();
        if (dir is null) return;
        using var tx = new SherpaOfflineTranscriber(dir);
        await tx.StartAsync(CancellationToken.None);
        await tx.TranscribeWindowAsync(Array.Empty<byte>(), 0, "m");
    }
}
