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
        // up to ~0.4 s (254 / 6046 / 11870 ms). Bands are deliberately wide so a third
        // silero variant drifting a few hundred ms cannot cause a false failure; a wrong
        // anchoring (e.g. per-push rebasing) lands >= 5 s off, so the discrimination
        // survives. Asset identity: models/silero_vad.onnx is the plain release file,
        // sha256 9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6, from
        // https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx.
        Assert.InRange(utterances[0].StartMs, 0, 650);
        Assert.InRange(utterances[1].StartMs, 5500, 6600);
        Assert.InRange(utterances[2].StartMs, 11100, 12300);
        Assert.InRange(utterances[0].Samples16k.Length, 76000, 81000);   // ~4.9 s of speech
        Assert.True(utterances[0].EndMs > utterances[0].StartMs);
    }

    [Fact]
    public void FlushClosesPendingSpeech()
    {
        if (!File.Exists(ModelPath)) return;
        var all = TestAudio.ReadWav16k(FixturePath);
        using var seg = new UtteranceSegmenter(ModelPath, minSilenceSeconds: 0.5f);
        // Feed only the first 50000 samples (voice A's clip, 3.125 s) in the pump's
        // 3200-sample chunks: the 0.5 s silence gate is never reached inside this span,
        // so nothing may close before Flush. Flush must emit the WHOLE pending speech
        // as one utterance — start at the detected speech onset, end at the last full
        // VAD window boundary (~49664 samples = 3.104 s). This pins two failure modes:
        // a fragment-only Flush (emitting just its ~0.31 s tail window) fails the
        // > 40000-sample assert, and any pre-flush closure fails Single.
        var got = new List<Utterance>();
        for (var off = 0; off < 50000; off += 3200)
        {
            var n = Math.Min(3200, 50000 - off);
            got.AddRange(seg.Push(all.AsSpan(off, n).ToArray()));
        }
        got.AddRange(seg.Flush());

        Assert.Single(got);
        Assert.InRange(got[0].StartMs, 150, 450);
        Assert.True(got[0].Samples16k.Length > 40000, "expected the whole pending speech, got " + got[0].Samples16k.Length + " samples");
        Assert.InRange(got[0].EndMs, 3050, 3160);
    }
}
