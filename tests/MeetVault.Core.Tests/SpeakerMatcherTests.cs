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
    }

    private static float[] Vec(params float[] v) { var r = new float[8]; Array.Copy(v, r, v.Length); return r; }
    private static float[] Jitter(float[] v, float d) { var r = (float[])v.Clone(); r[0] += d; return r; }

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
}
