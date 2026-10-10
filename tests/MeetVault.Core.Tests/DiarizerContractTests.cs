using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The repaired diarizer contract (Phase 3): real audio in, stable hints out; empty
// audio is a neutral Unknown, never a throw; the old fake empty-array path is gone.
public sealed class DiarizerContractTests
{
    private static SherpaDiarizer? TryOpen()
    {
        var seg = Path.Combine(TestAudio.RepoRoot, "models", "sherpa-seg.onnx");
        var emb = Path.Combine(TestAudio.RepoRoot, "models", "sherpa-emb.onnx");
        if (!File.Exists(seg) || !File.Exists(emb)) return null; // models not staged; nothing to verify
        return new SherpaDiarizer(seg, emb);
    }

    [Fact]
    public void AssignTurn_EmptyAudio_ReturnsUnknown_NoThrow()
    {
        using var dz = TryOpen();
        if (dz is null) return;

        var hint = dz.AssignTurn(new TurnAudio(ReadOnlyMemory<float>.Empty, 0, 3000));

        Assert.Equal("", hint.SpeakerId);
        Assert.Equal(SpeakerOrigin.Unknown, hint.Origin);
    }

    [Fact]
    public void AssignTurn_TwoVoices_DistinguishesAndStabilizes()
    {
        // Synthetic A/B/A fixture (Windows TTS voices; committed, no consent required).
        // Two-sighting rule (SpeakerMatcher): an unmatched voice keeps the previous
        // speaker on its FIRST sighting and owns a new spk_N only once seen again.
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;

        var all = TestAudio.ReadWav16k(wavPath);
        var a1 = dz.AssignTurn(Slice(all, 0, 90720));        // voice A, first sighting
        var b1 = dz.AssignTurn(Slice(all, 95520, 183040));   // voice B, first sighting -> keeps previous
        var b2 = dz.AssignTurn(Slice(all, 95520, 183040));   // voice B again -> own identity
        var a2 = dz.AssignTurn(Slice(all, 0, 90720));        // voice A again -> stable
        var again = dz.AssignTurn(Slice(all, 60480, 90720)); // A sub-clip -> still stable

        Assert.StartsWith("spk_", a1.SpeakerId);
        Assert.Equal(SpeakerOrigin.Embedding, a1.Origin);
        Assert.Equal(a1.SpeakerId, a2.SpeakerId);       // same voice keeps its id
        Assert.Equal(a1.SpeakerId, again.SpeakerId);
        Assert.NotEqual(a1.SpeakerId, b2.SpeakerId);    // different voice gets its own id once confirmed
    }

    [Fact]
    public void AssignTurn_SubClipsOfOneVoice_StayOneSpeaker()
    {
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;
        var all = TestAudio.ReadWav16k(wavPath);
        // Voice A split into three ~1.9 s pieces must all resolve to the same speaker.
        var p1 = dz.AssignTurn(Slice(all, 0, 30240));
        var p2 = dz.AssignTurn(Slice(all, 30240, 60480));
        var p3 = dz.AssignTurn(Slice(all, 60480, 90720));
        Assert.Equal(p1.SpeakerId, p2.SpeakerId);
        Assert.Equal(p1.SpeakerId, p3.SpeakerId);
        // Voice B: the first sighting keeps the previous speaker (two-sighting rule); the second
        // sighting confirms its own identity, distinct from A's.
        var b1 = dz.AssignTurn(Slice(all, 95520, 139280));
        var b2 = dz.AssignTurn(Slice(all, 139280, 183040));
        Assert.Equal(p1.SpeakerId, b1.SpeakerId);
        Assert.NotEqual(p1.SpeakerId, b2.SpeakerId);
        // Hard recall pin: after b2, _last is spk_2, so this can only resolve to p1's id via a
        // real store hit on A's stored embeddings (a recall miss would return the previous id).
        var p4 = dz.AssignTurn(Slice(all, 60480, 90720));
        Assert.Equal(p1.SpeakerId, p4.SpeakerId);
    }

    private static TurnAudio Slice(float[] all, int start, int end)
        => new(new ReadOnlyMemory<float>(all, start, end - start), start * 1000L / 16000, end * 1000L / 16000);
}
