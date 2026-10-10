using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Sentence segmentation: silence >= MinSilenceDuration ends an utterance.
// Uses the real silero model; skips when models/silero_vad.onnx is not staged.
public sealed class UtteranceSegmenterTests
{
    private static string ModelPath => Path.Combine(TestAudio.RepoRoot, "models", "silero_vad.onnx");
    private static string FixturePath => Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");

    [Fact]
    public void SplitsFixtureAtNaturalPauses()
    {
        if (!File.Exists(ModelPath)) return;
        var all = TestAudio.ReadWav16k(FixturePath);
        using var seg = new UtteranceSegmenter(ModelPath, minSilenceSeconds: 0.2f);

        var utterances = new List<Utterance>();
        // Feed in 3200-sample (200 ms) chunks, like the live pump delivers frames.
        for (var off = 0; off < all.Length; off += 3200)
        {
            var n = Math.Min(3200, all.Length - off);
            utterances.AddRange(seg.Push(all.AsSpan(off, n).ToArray()));
        }
        utterances.AddRange(seg.Flush());

        Assert.Equal(3, utterances.Count);
        // Measured (plain/v4 silero_vad.onnx, 3200-sample chunks): starts 4192 / 97376 /
        // 196192 samples. The VAD trims each TTS clip's quiet tail/onset, so starts sit
        // a few hundred ms after the raw region boundaries (0 / 95520 / 187840); they are
        // absolute in the fed stream, which is what these ranges pin. v5 shifts them by
        // up to ~0.4 s (254 / 6046 / 11870 ms), hence the margins.
        Assert.InRange(utterances[0].StartMs, 150, 450);
        Assert.InRange(utterances[1].StartMs, 5850, 6350);
        Assert.InRange(utterances[2].StartMs, 11600, 12500);
        Assert.InRange(utterances[0].Samples16k.Length, 76000, 81000);   // ~4.9 s of speech
        Assert.True(utterances[0].EndMs > utterances[0].StartMs);
    }

    [Fact]
    public void FlushClosesPendingSpeech()
    {
        if (!File.Exists(ModelPath)) return;
        var all = TestAudio.ReadWav16k(FixturePath);
        using var seg = new UtteranceSegmenter(ModelPath, minSilenceSeconds: 0.5f);
        // One single Push of the whole file: sherpa aggregates the speech decision per
        // AcceptWaveform call, so nothing closes mid-stream here and the pending speech
        // is emitted by Flush alone (start_ = tail - 2*512 - minSpeech, i.e. the last
        // ~0.31 s window; the pump never feeds frames this large).
        var got = seg.Push(all).Concat(seg.Flush()).ToList();
        Assert.Single(got);
        Assert.InRange(got[0].EndMs, 17300, 17500);
    }
}
