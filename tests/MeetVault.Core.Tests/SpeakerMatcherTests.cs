using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class SpeakerMatcherTests
{
    private sealed class FakeStore : IEmbeddingStore
    {
        public readonly Dictionary<string, List<float[]>> ById = new();
        public string Search(float[] emb, float threshold)
        {
            var best = ""; var bestSim = threshold;
            foreach (var (id, list) in ById)
                foreach (var e in list)
                {
                    var s = SpeakerMatcher.Cosine(emb, e);
                    if (s >= bestSim) { bestSim = s; best = id; }
                }
            return best;
        }
        public void Add(string id, float[] emb)
        {
            if (!ById.TryGetValue(id, out var list)) ById[id] = list = new();
            list.Add(emb);
        }
        public void Clear() => ById.Clear();
    }

    private static float[] Vec(params float[] v) { var r = new float[8]; Array.Copy(v, r, v.Length); return r; }
    private static float[] Jitter(float[] v, float d) { var r = (float[])v.Clone(); r[0] += d; return r; }
    private static float[] Dir(double deg) => Vec((float)Math.Cos(deg * Math.PI / 180), (float)Math.Sin(deg * Math.PI / 180));

    [Fact]
    public void SameEmbedding_Twice_KeepsOneIdentity()
    {
        var m = new SpeakerMatcher(new FakeStore());
        var a = Vec(1, 0);
        Assert.Equal("spk_1", m.Assign(a, 0).SpeakerId);
        Assert.Equal("spk_1", m.Assign(a, 1000).SpeakerId);
    }

    [Fact]
    public void UnmatchedEmbedding_Once_DoesNotMintIdentity()
    {
        var m = new SpeakerMatcher(new FakeStore());
        var a = Vec(1, 0); var b = Vec(0, 1);
        m.Assign(a, 0);
        var r = m.Assign(b, 1000);
        Assert.Equal("spk_1", r.SpeakerId);       // keeps previous speaker, no new id
        Assert.Equal(SpeakerOrigin.Embedding, r.Origin);
    }

    [Fact]
    public void UnmatchedEmbedding_Twice_MintsIdentity()
    {
        var m = new SpeakerMatcher(new FakeStore());
        var a = Vec(1, 0); var b = Vec(0, 1);
        m.Assign(a, 0);
        m.Assign(b, 1000);
        var r = m.Assign(Jitter(b, 0.01f), 2000); // ~same as pending candidate
        Assert.Equal("spk_2", r.SpeakerId);
        Assert.True(r.IsNew);
    }

    [Fact]
    public void PendingCandidate_MatchesOnSimilarity_NotEquality()
    {
        var m = new SpeakerMatcher(new FakeStore());
        var a = Vec(1, 0); var b = Vec(0, 1);
        m.Assign(a, 0);
        m.Assign(b, 1000);                         // pending 1
        m.Assign(Jitter(b, 0.05f), 1500);          // pending 2 (same candidate)
        var r = m.Assign(Jitter(b, 0.08f), 2000);
        Assert.Equal("spk_2", r.SpeakerId);
    }

    [Fact]
    public void DifferentVoices_NeverCollide()
    {
        var m = new SpeakerMatcher(new FakeStore());
        m.Assign(Vec(1, 0), 0);
        m.Assign(Vec(0, 1), 100);
        var r = m.Assign(Vec(0, 1), 200);
        Assert.Equal("spk_2", r.SpeakerId);
        Assert.Equal("spk_1", m.Assign(Vec(1, 0), 300).SpeakerId);
    }

    [Fact]
    public void ConfirmedCandidate_KeepsFirstSightingEmbedding()
    {
        var store = new FakeStore();
        var m = new SpeakerMatcher(store);
        var a = Vec(1, 0); var b = Vec(0, 1);
        m.Assign(a, 0);
        m.Assign(b, 1000);                    // first sighting of the new voice
        var confirm = Jitter(b, 0.01f);
        m.Assign(confirm, 2000);              // second sighting confirms it

        Assert.True(store.ById.TryGetValue("spk_2", out var embeddings));
        Assert.Equal(2, embeddings.Count);    // both sightings, not only the confirmer
        Assert.Contains(embeddings, e => SpeakerMatcher.Cosine(e, b) == 1f);
        Assert.Contains(embeddings, e => SpeakerMatcher.Cosine(e, confirm) == 1f);
    }

    // --- review round 2 (argmax pending commit) ---

    // Two voices pending at once, both clearing 0.5 against the confirmer, and the newer
    // one is the better match (cos 0.866 vs 0.766). First-wins would commit the older
    // candidate; the closest one must win. Angles: a=0deg, P1=95deg, P2=165deg, C=135deg.
    [Fact]
    public void PendingCandidates_CloserOneCommits()
    {
        var m = new SpeakerMatcher(new FakeStore());
        m.Assign(Vec(1, 0), 0);                    // first sighting of voice 1 (spk_1)
        m.Assign(Dir(95), 1000);                   // pending A (spk_2)
        m.Assign(Dir(165), 2000);                  // pending B (spk_3)
        var r = m.Assign(Dir(135), 3000);          // clears both; closer to B

        Assert.Equal("spk_3", r.SpeakerId);
        Assert.True(r.IsNew);
    }

    // An exact tie keeps the older (first-scanned) candidate: P1/P2 are mirror images
    // around C, so both cosines are bit-identical (the shared x-component carries the
    // whole dot product). Angles: a=180deg, C=0deg, P1=45deg, P2=-45deg.
    [Fact]
    public void PendingCandidates_Tie_CommitsOldest()
    {
        var m = new SpeakerMatcher(new FakeStore());
        m.Assign(Vec(-1, 0), 0);                   // pending spk_1
        m.Assign(Dir(45), 1000);                   // pending spk_2
        m.Assign(Dir(-45), 2000);                  // pending spk_3
        var r = m.Assign(Vec(1, 0), 3000);         // equidistant

        Assert.Equal("spk_2", r.SpeakerId);
    }

    // --- review round 4 (per-meeting reset) ---

    // Reset() must give the next meeting a clean identity space: the old embedding store is
    // emptied (no recall of the previous meeting's voices), pending candidates are dropped,
    // and the id counter/last id restart at the placeholder — so a second meeting on the
    // same matcher mints spk_1 for its first confirmed voice again, never spk_3+.
    [Fact]
    public void Reset_ForgetsThePreviousMeeting_IdsRestartAtSpk_1()
    {
        var m = new SpeakerMatcher(new FakeStore());
        var a = Vec(1, 0); var b = Vec(0, 1); var c = Vec(0, 0, 1);

        // Meeting one: a and b confirmed (spk_1, spk_2); c left pending (reserved spk_3).
        m.Assign(a, 0);
        Assert.Equal("spk_1", m.Assign(a, 1000).SpeakerId);
        m.Assign(b, 2000);
        Assert.Equal("spk_2", m.Assign(b, 3000).SpeakerId);
        m.Assign(c, 4000);

        m.Reset();

        // Old embeddings are forgotten: a needs both sightings again (a store hit would
        // keep IsNew=false on the second).
        Assert.Equal("spk_1", m.Assign(a, 0).SpeakerId);
        var aAgain = m.Assign(a, 1000);
        Assert.Equal("spk_1", aAgain.SpeakerId);
        Assert.True(aAgain.IsNew);

        // The pending candidate is dropped: c's first sighting is fresh again (a surviving
        // pending entry would confirm the old reservation spk_3 on the spot).
        var cFirst = m.Assign(c, 2000);
        Assert.Equal("spk_1", cFirst.SpeakerId);
        Assert.False(cFirst.IsNew);
        // The counter restarted: c confirms as the new meeting's second voice -> spk_2.
        var cSecond = m.Assign(c, 3000);
        Assert.Equal("spk_2", cSecond.SpeakerId);
        Assert.True(cSecond.IsNew);
    }
}
