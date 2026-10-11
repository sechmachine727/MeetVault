using SherpaOnnx;

namespace MeetVault.Core;

// Real diarization behind ISpeakerDiarizer: sherpa-onnx 1.13.8 offline diarization (finalized windows).
// Identity is tracked ACROSS turns by SpeakerMatcher (two-sighting confirmation over a
// multi-embedding store), because each Process() call only clusters within its own window —
// local indices restart every turn.
public sealed class SherpaDiarizer : ISpeakerDiarizer, IDisposable
{
    private readonly OfflineSpeakerDiarization _diar;
    private readonly SpeakerEmbeddingExtractor _embed;
    private readonly SpeakerMatcher _matcher;

    public SherpaDiarizer(string segmentationModel, string embeddingModel, int numThreads = 1)
    {
        if (!File.Exists(segmentationModel)) throw new FileNotFoundException($"Diarization segmentation model not found: {segmentationModel}.", segmentationModel);
        if (!File.Exists(embeddingModel)) throw new FileNotFoundException($"Diarization embedding model not found: {embeddingModel}.", embeddingModel);
        var config = new OfflineSpeakerDiarizationConfig
        {
            Segmentation = new OfflineSpeakerSegmentationModelConfig
            {
                Pyannote = new OfflineSpeakerSegmentationPyannoteModelConfig { Model = segmentationModel },
                NumThreads = numThreads,
            },
            Embedding = new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = numThreads },
        };
        _diar = new OfflineSpeakerDiarization(config);
        _embed = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = numThreads });
        _matcher = new SpeakerMatcher(new ManagerStore());
        AppLog.Info("diarizer: loaded seg=" + System.IO.Path.GetFileName(segmentationModel) + " emb=" + System.IO.Path.GetFileName(embeddingModel) + " threads=" + numThreads);
    }

    // Assigns the dominant speaker of one finalized 16 kHz mono turn as a stable spk_N id.
    // Empty audio carries no speaker: returns an Unknown hint rather than throwing, and
    // never invents an identity for a turn it cannot hear. A failure inside the sherpa
    // natives degrades the same way (Warn + Unknown, keeping the caller's previous
    // speaker): the LABEL is lost, never the caller's transcription — a throw here would
    // abort the live path and silently lose the utterance's whole text. The old
    // WindowDiarizer degraded identically (kept the current label on failure).
    public SpeakerHint AssignTurn(TurnAudio turn)
    {
        if (turn.Samples16kMono.IsEmpty) return Unknown;
        try
        {
            var samples = turn.Samples16kMono;
            var segments = _diar.Process(samples.ToArray());
            if (segments.Length == 0) return Unknown;

            var dominant = segments.OrderByDescending(s => s.End - s.Start).First().Speaker;
            var emb = EmbeddingFor(samples.Span, segments.Where(s => s.Speaker == dominant));

            var match = _matcher.Assign(emb, turn.StartMs);
            return new SpeakerHint(match.SpeakerId, match.Origin);
        }
        catch (Exception ex)
        {
            AppLog.Warn("diarizer: AssignTurn failed, using the Unknown hint :: " + ex.Message);
            return Unknown;
        }
    }

    // New-meeting reset (called by LivePage.Start_Click, never on recovery): the matcher's
    // ids/embeddings restart at spk_1 so a second meeting started on the same diarizer
    // instance cannot continue the previous meeting's spk_N numbering or match its voices.
    public void Reset()
    {
        _matcher.Reset();
        AppLog.Info("diarizer: matcher reset for a new meeting (ids restart at spk_1)");
    }

    // Union of this local speaker's spans within the turn -> one embedding per turn.
    private float[] EmbeddingFor(ReadOnlySpan<float> samples, IEnumerable<OfflineSpeakerDiarizationSegment> spans)
    {
        var parts = new List<float>();
        foreach (var s in spans)
        {
            var from = Math.Clamp((int)(s.Start * 16000), 0, samples.Length);
            var to = Math.Clamp((int)(s.End * 16000), from, samples.Length);
            for (var i = from; i < to; i++) parts.Add(samples[i]);
        }
        if (parts.Count == 0) parts.AddRange(samples.ToArray());
        using var stream = _embed.CreateStream();
        stream.AcceptWaveform(16000, parts.ToArray());
        stream.InputFinished();
        return _embed.Compute(stream);
    }

    // Embedding store for SpeakerMatcher. Owns its own per-id embedding lists instead of
    // using SpeakerEmbeddingManager: measured on sherpa-onnx 1.13.8 (throwaway diag,
    // 2026-10-11: Add(id, v1)=True, Add(id, v2)=False, Search(v1) hits, Search(v2) misses),
    // a second Add for an existing id returns false and the embedding is silently dropped —
    // the manager neither appends nor replaces, so its store cannot grow with the meeting.
    // Search = best cosine over the lists ("" when nothing reaches the threshold);
    // Add appends, keeping the newest 5 embeddings per id.
    private sealed class ManagerStore : IEmbeddingStore
    {
        private const int MaxPerId = 5;
        private readonly Dictionary<string, List<float[]>> _byId = new();

        public string Search(float[] embedding, float threshold)
        {
            var best = "";
            var bestSim = threshold;
            foreach (var (id, list) in _byId)
                foreach (var e in list)
                {
                    var s = SpeakerMatcher.Cosine(embedding, e);
                    if (s >= bestSim) { bestSim = s; best = id; }
                }
            return best;
        }

        public void Add(string speakerId, float[] embedding)
        {
            if (!_byId.TryGetValue(speakerId, out var list)) _byId[speakerId] = list = new();
            list.Add(embedding);
            if (list.Count > MaxPerId) list.RemoveAt(0);
        }

        public void Clear() => _byId.Clear();
    }

    private static readonly SpeakerHint Unknown = new("", SpeakerOrigin.Unknown);

    public void Dispose() { _embed.Dispose(); _diar.Dispose(); }
}


