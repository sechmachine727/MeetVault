namespace MeetVault.Core;

// Storage seam over SpeakerEmbeddingManager so the matching policy is unit-testable
// without sherpa models.
public interface IEmbeddingStore
{
    string Search(float[] embedding, float threshold); // "" when nothing is close enough
    void Add(string speakerId, float[] embedding);
    void Clear(); // drops every stored embedding: a NEW meeting must not match the old voices
}

public sealed record SpeakerMatch(string SpeakerId, SpeakerOrigin Origin, bool IsNew);

// Decides a speaker for one utterance's embedding. Two rules keep identities stable:
//  1) a hit above MatchThreshold reuses the speaker;
//  2) an unmatched embedding must be seen TWICE (within PendingTtlMs) before it becomes
//     a new speaker — one-off noise/music windows keep the previous speaker instead of
//     minting a new identity per line (the owner-visible "speaker increments" bug).
// A pending candidate RESERVES its spk_N id at first sighting and COMMITS it at the
// second: the voice keeps one id even when another voice is confirmed in between
// (minting at confirmation would let an interleaved confirmation flip a voice's id),
// and the first voice's reserved id (spk_1) coincides with the placeholder, so its id
// never changes when it commits. An id consumed at first sighting stays consumed even
// if the candidate is evicted (PendingCap/TtlMs) or never confirmed — gaps such as
// spk_1, spk_3 are accepted by design, because the reservation must stay stable from
// first sighting.
public sealed class SpeakerMatcher
{
    private const float MatchThreshold = 0.5f;
    private const float PendingThreshold = 0.5f;
    private const int ConfirmCount = 2;
    private const int PendingCap = 6;
    private const long PendingTtlMs = 120_000;

    private readonly IEmbeddingStore _store;
    private readonly List<(float[] Emb, int Count, long FirstMs, long LastMs, string Reserved)> _pending = new();
    private int _next = 1;
    private string _last = "spk_1";

    public SpeakerMatcher(IEmbeddingStore store) => _store = store;

    // Starts a fresh identity space for a NEW meeting: pending candidates, the id counter
    // and the last id restart at the placeholder, and the embedding store is emptied so the
    // new meeting's voices never match the previous meeting's embeddings (or continue its
    // spk_N numbering). NEVER call this on a recovery path: a recovered meeting must keep
    // its existing spk_N ids and matches.
    public void Reset()
    {
        _pending.Clear();
        _next = 1;
        _last = "spk_1";
        _store.Clear();
    }

    // Prepares this matcher to CONTINUE an existing meeting after a crash (recovery).
    // The persisted draft rows define the identity space: the id counter restarts ABOVE
    // every existing spk_N (speakers and segment ids alike; malformed ids are ignored)
    // and the last id follows the draft's tail. A fresh matcher would otherwise reserve
    // spk_1/spk_2 again, so the first voice confirmed after a resume could mint an id
    // that already belongs to another voice's persisted row — two voices swapping
    // identities (and display names) for the rest of the meeting.
    // Pending candidates and the embedding store are cleared (the same rule Reset
    // enforces), and nothing is seeded INTO the store: embeddings are never persisted
    // with a draft, so recall cannot be restored. Honest consequences: a reappearing
    // voice mints a NEW id rather than colliding with its old row, and a single-voice
    // continuation keeps the last speaker's id on its first sighting (the normal
    // two-sighting rule applies from there).
    // NEVER call this for a new meeting — that is Reset()'s job; never call Reset() on
    // a recovery (it would restart the ids at spk_1).
    public void SeedForRecovery(IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments)
    {
        _pending.Clear();
        _store.Clear();
        var max = 0;
        foreach (var s in speakers) max = Math.Max(max, ParseId(s.Id));
        foreach (var s in segments) max = Math.Max(max, ParseId(s.SpeakerId));
        _next = max + 1;
        _last = "spk_1";
        var last = "";
        for (var i = segments.Count - 1; i >= 0 && last.Length == 0; i--) last = segments[i].SpeakerId ?? "";
        if (last.Length == 0)
            for (var i = speakers.Count - 1; i >= 0 && last.Length == 0; i--) last = speakers[i].Id ?? "";
        if (last.Length > 0) _last = last;
        AppLog.Info("matcher: seeded for recovery next=spk_" + _next + " last=" + _last
            + " speakers=" + speakers.Count + " segments=" + segments.Count);
    }

    // "spk_N" -> N; anything else -> 0, and an absurd N cannot overflow the counter.
    // A malformed row must never move the id space.
    private static int ParseId(string? id)
    {
        if (id is null || !id.StartsWith("spk_", StringComparison.Ordinal)) return 0;
        return int.TryParse(id.AsSpan(4), out var n) ? Math.Min(n, int.MaxValue - 1) : 0;
    }

    public SpeakerMatch Assign(float[] embedding, long atMs)
    {
        var id = _store.Search(embedding, MatchThreshold);
        if (id.Length > 0)
        {
            _store.Add(id, embedding);   // multi-embedding store: recall grows with the meeting (capped at 5 embeddings per id)
            _last = id;
            return new SpeakerMatch(id, SpeakerOrigin.Embedding, false);
        }

        _pending.RemoveAll(p => atMs - p.LastMs > PendingTtlMs);
        // Commit the CLOSEST pending candidate that clears the threshold, not merely the
        // first in scan order: with two voices pending at once, first-wins let an older,
        // less-similar candidate win the confirmation. A tie keeps the older entry
        // (strictly-greater comparison over the append-ordered list).
        var best = -1;
        var bestSim = PendingThreshold;
        for (var i = 0; i < _pending.Count; i++)
        {
            var sim = Cosine(embedding, _pending[i].Emb);
            if (sim < PendingThreshold) continue;
            if (best < 0 || sim > bestSim) { best = i; bestSim = sim; }
        }
        if (best >= 0)
        {
            var p = _pending[best];
            var count = p.Count + 1;
            if (count >= ConfirmCount)
            {
                // Keep BOTH sightings: the candidate's first utterance (p.Emb) and the one
                // that confirms it. Storing only the confirmer dropped the voice's first
                // sample from the search store, so later utterances matched on half the
                // evidence. The store caps at 5 embeddings per id (oldest dropped).
                _store.Add(p.Reserved, p.Emb);
                _store.Add(p.Reserved, embedding);
                _pending.RemoveAt(best);
                _last = p.Reserved;
                AppLog.Debug("matcher: confirmed new speaker " + p.Reserved);
                return new SpeakerMatch(p.Reserved, SpeakerOrigin.Embedding, true);
            }
            // Unreachable while ConfirmCount == 2 (the first match already reaches count 2); kept so raising ConfirmCount still behaves.
            _pending[best] = (p.Emb, count, p.FirstMs, atMs, p.Reserved);
            return new SpeakerMatch(_last, SpeakerOrigin.Embedding, false);
        }

        _pending.Add((embedding, 1, atMs, atMs, $"spk_{_next++}"));
        if (_pending.Count > PendingCap) _pending.RemoveAt(0);
        return new SpeakerMatch(_last, SpeakerOrigin.Embedding, false);
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 1e-9 ? 0f : (float)(dot / denom);
    }
}
