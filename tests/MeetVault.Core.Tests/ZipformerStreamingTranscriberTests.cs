using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class ZipformerStreamingTranscriberTests
{
    private static string? ModelDir()
    {
        var dir = Path.Combine(TestAudio.RepoRoot, "models", "zipformer", "20m-en");
        return File.Exists(Path.Combine(dir, "encoder-epoch-99-avg-1.int8.onnx")) ? dir : null;
    }

    [Fact]
    public async Task StreamsPartials_AndFinalizesTheFixture()
    {
        var dir = ModelDir();
        if (dir is null) return;
        var wav = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        var all = TestAudio.ReadWav16k(wav);
        using var tx = new ZipformerStreamingTranscriber(dir);
        var segs = new List<Segment>();
        var partials = new List<string>();
        tx.SegmentReady += segs.Add;
        tx.PartialReady += partials.Add;
        await tx.StartAsync(CancellationToken.None);
        for (var i = 0; i + 1600 <= all.Length; i += 1600)
        {
            var chunk = new float[1600];
            Array.Copy(all, i, chunk, 0, 1600);
            tx.Push(chunk);
            await Task.Delay(1);
        }
        tx.Flush();
        await tx.StopAsync(CancellationToken.None);
        Assert.NotEmpty(partials.Where(p => p.Length > 0));
        Assert.NotEmpty(segs);
        // The head of the FIRST utterance only survives because StartAsync primes the
        // stream with leading silence; without the preroll this segment starts mid-word
        // ("'S LAZY DOG...") and contains no "fox".
        Assert.Contains("fox", segs[0].Text, StringComparison.OrdinalIgnoreCase);
        // The engine export is ALL CAPS; the final text must arrive sentence-cased.
        Assert.True(char.IsUpper(segs[0].Text[0]), "expected sentence case, got: " + segs[0].Text);
        Assert.NotEqual(segs[0].Text, segs[0].Text.ToUpperInvariant());
    }
}
