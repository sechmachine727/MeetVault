using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class VoskStreamingTranscriberTests
{
    private static string? ModelDir()
    {
        var dir = Path.Combine(TestAudio.RepoRoot, "models", "vosk", "small-en");
        return File.Exists(Path.Combine(dir, "am", "final.mdl")) ? dir : null;
    }

    [Fact]
    public void JsonText_ParsesBothShapes()
    {
        Assert.Equal("hello world", VoskJson.Text("{\"text\": \"hello world\"}", "text"));
        Assert.Equal("hel", VoskJson.Text("{\"partial\": \"hel\"}", "partial"));
        Assert.Equal("", VoskJson.Text("not json", "text"));
        Assert.Equal("", VoskJson.Text("{}", "text"));
    }

    [Fact]
    public async Task StreamsPartials_AndFinalizesTheFixture()
    {
        var dir = ModelDir();
        if (dir is null) return;
        var wav = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        var all = TestAudio.ReadWav16k(wav);
        using var tx = new VoskStreamingTranscriber(dir);
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
        // At least one ENDPOINT final must exist besides the flush final: the fixture holds
        // three sentences, so a single-segment result would mean Vosk's own endpoints never
        // fired (only the flush closed the stream).
        Assert.True(segs.Count >= 2, "expected an endpoint final, got " + segs.Count + " segment(s)");
        Assert.Contains("fox", segs[0].Text, StringComparison.OrdinalIgnoreCase);
    }
}
