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
    public void AssignTurn_InternalFailure_DegradesToUnknown_NoThrow()
    {
        // A diarizer failure must degrade the LABEL, never abort the caller's transcription:
        // in the live path a throw from here used to propagate into a catch { } that dropped
        // the utterance's whole text. Fault injection: null the private native diarizer
        // handle so the body throws a managed NullReferenceException at its first native use
        // (a real sherpa failure is not reproducible deterministically; a disposed handle
        // would risk a native access violation instead of a managed throw).
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;
        var field = typeof(SherpaDiarizer).GetField("_diar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var real = field.GetValue(dz);
        try
        {
            field.SetValue(dz, null);
            Assert.Null(field.GetValue(dz)); // the injection must actually bite (a no-op set would leave the real handle)

            // A non-silent two-speaker fixture slice, which normally resolves to a spk_
            // hint: Unknown can now ONLY come from the injected failure — a silent clip or
            // a no-op injection would yield Unknown regardless and prove nothing.
            var all = TestAudio.ReadWav16k(wavPath);
            var hint = dz.AssignTurn(Slice(all, 0, 90720));

            Assert.Equal("", hint.SpeakerId);
            Assert.Equal(SpeakerOrigin.Unknown, hint.Origin);
        }
        finally
        {
            field.SetValue(dz, real); // restore so Dispose() can run
        }
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
        var a2 = dz.AssignTurn(Slice(all, 0, 90720));        // voice A again -> own identity
        var b1 = dz.AssignTurn(Slice(all, 95520, 183040));   // voice B, first sighting -> keeps previous
        var b2 = dz.AssignTurn(Slice(all, 95520, 183040));   // voice B again -> own identity
        // A sub-clip with _last now B's id: this can only resolve to A via a real store
        // recall of A's embeddings — a recall miss would return the previous id (B) and
        // fail the assert below (the old order asserted this while _last was still A, so
        // the assert could not fail).
        var again = dz.AssignTurn(Slice(all, 60480, 90720));

        Assert.StartsWith("spk_", a1.SpeakerId);
        Assert.Equal(SpeakerOrigin.Embedding, a1.Origin);
        Assert.Equal(a1.SpeakerId, a2.SpeakerId);       // same voice keeps its id
        Assert.Equal(a1.SpeakerId, b1.SpeakerId);       // B's first sighting keeps the previous speaker
        Assert.Equal(a1.SpeakerId, again.SpeakerId);    // recall pin: a miss would return B
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

    // A second meeting on the same diarizer must not match the previous meeting's voices
    // or continue its spk_N numbering: after Reset, voice B's first sighting is a fresh
    // candidate again (a surviving store would recall its old spk_2) and the confirmed
    // voice mints spk_1 again rather than spk_3.
    [Fact]
    public void Reset_ForgetsThePreviousMeeting_VoicesAndIdsRestart()
    {
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;

        var all = TestAudio.ReadWav16k(wavPath);
        // Meeting one: A confirmed (spk_1), B confirmed (spk_2).
        dz.AssignTurn(Slice(all, 0, 90720));
        Assert.Equal("spk_1", dz.AssignTurn(Slice(all, 0, 90720)).SpeakerId);
        dz.AssignTurn(Slice(all, 95520, 183040));
        Assert.Equal("spk_2", dz.AssignTurn(Slice(all, 95520, 183040)).SpeakerId);

        dz.Reset();

        // Old recall is gone: B's first post-reset sighting keeps the (reset) placeholder
        // instead of matching its stored spk_2.
        Assert.Equal("spk_1", dz.AssignTurn(Slice(all, 95520, 183040)).SpeakerId);
        // ...and its confirmation mints spk_1 again (a counter that kept running -> spk_3).
        Assert.Equal("spk_1", dz.AssignTurn(Slice(all, 95520, 183040)).SpeakerId);
    }

    // --- review round 5 (recovery seeding) ---

    // A crashed two-voice meeting (spk_1 = A, spk_2 = B, B spoke last) is recovered. The
    // resumed matcher must not mint ids that collide with the persisted rows: unseeded, a
    // fresh matcher (what the app builds at page load; Reset() leaves exactly that state)
    // would reserve spk_1 for the first voice confirmed after the resume — B would
    // silently become A, and later A would become B (the identity swap). Seeded, the
    // reappearing voice mints a NEW id instead (recall cannot be restored: embeddings are
    // not persisted, so both voices come back as fresh ids above the draft).
    [Fact]
    public void SeedForRecovery_TwoVoiceDraft_ReappearingVoiceMintsNewId_NoCollision()
    {
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;

        var all = TestAudio.ReadWav16k(wavPath);
        // Meeting one produced the draft's rows: A confirmed as spk_1, B as spk_2.
        dz.AssignTurn(Slice(all, 0, 90720));
        Assert.Equal("spk_1", dz.AssignTurn(Slice(all, 0, 90720)).SpeakerId);
        dz.AssignTurn(Slice(all, 95520, 183040));
        Assert.Equal("spk_2", dz.AssignTurn(Slice(all, 95520, 183040)).SpeakerId);

        // Crash + relaunch: a fresh matcher (Reset leaves exactly the state a new page's
        // diarizer has) is seeded from the draft's rows before capture restarts.
        dz.Reset();
        dz.SeedForRecovery(
            new[] { new Speaker("spk_1", "A"), new Speaker("spk_2", "B") },
            new[] { new Segment("s1", "spk_1", 0, 2000, "a", true), new Segment("s2", "spk_2", 2000, 4000, "b", true) });

        // B reappears: its first sighting keeps the seeded last speaker (its own old row)...
        var b1 = dz.AssignTurn(Slice(all, 95520, 183040));
        Assert.Equal("spk_2", b1.SpeakerId);
        // ...and its confirmation mints a NEW id ABOVE every persisted row. Without the
        // seed this is "spk_1" — the collision that swaps A and B for the rest of the meeting.
        var b2 = dz.AssignTurn(Slice(all, 95520, 183040));
        Assert.Equal("spk_3", b2.SpeakerId);
        Assert.Equal(SpeakerOrigin.Embedding, b2.Origin);
        // A reappears: a fresh id too (spk_4), never its old colliding row.
        Assert.Equal("spk_3", dz.AssignTurn(Slice(all, 0, 90720)).SpeakerId); // first sighting keeps the previous speaker
        var a2 = dz.AssignTurn(Slice(all, 0, 90720));
        Assert.Equal("spk_4", a2.SpeakerId);
        Assert.Equal(SpeakerOrigin.Embedding, a2.Origin);
    }

    // A single-voice draft: the continuing voice keeps the last speaker's id on its first
    // sighting (the resumed tail does not suddenly read as a brand-new speaker), then the
    // normal two-sighting rule mints a fresh id (spk_2) — recall cannot be restored
    // without persisted embeddings. The point is that nothing collides with spk_1.
    [Fact]
    public void SeedForRecovery_SingleVoiceDraft_ContinuationKeepsTheLastId()
    {
        var wavPath = Path.Combine(TestAudio.RepoRoot, "tests", "MeetVault.Core.Tests", "fixtures", "diarizer_two_speakers_16k.wav");
        Assert.True(File.Exists(wavPath), "fixture missing: " + wavPath);
        using var dz = TryOpen();
        if (dz is null) return;

        var all = TestAudio.ReadWav16k(wavPath);
        // A fresh diarizer (as at page load) seeded from a crashed single-voice draft.
        dz.SeedForRecovery(
            new[] { new Speaker("spk_1", "A") },
            new[] { new Segment("s1", "spk_1", 0, 2000, "a", true) });

        var a1 = dz.AssignTurn(Slice(all, 0, 90720));
        Assert.Equal("spk_1", a1.SpeakerId);   // keeps the last speaker's id
        var a2 = dz.AssignTurn(Slice(all, 0, 90720));
        Assert.Equal("spk_2", a2.SpeakerId);   // two-sighting confirmation: fresh id above the draft
    }

    private static TurnAudio Slice(float[] all, int start, int end)
        => new(new ReadOnlyMemory<float>(all, start, end - start), start * 1000L / 16000, end * 1000L / 16000);
}
