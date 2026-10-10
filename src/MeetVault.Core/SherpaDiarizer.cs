using SherpaOnnx;

namespace MeetVault.Core;

// Real diarization behind ISpeakerDiarizer: sherpa-onnx 1.13.8 offline diarization (finalized windows).
// Identity is tracked ACROSS turns by speaker embedding (cosine match), because each
// Process() call only clusters within its own window — local indices restart every turn.
public sealed class SherpaDiarizer : ISpeakerDiarizer, IDisposable
{
    private readonly OfflineSpeakerDiarization _diar;
    private readonly SpeakerEmbeddingExtractor _embed;
    private readonly SpeakerEmbeddingManager _manager;
    private int _next = 1;

    // Cosine threshold for matching a turn's embedding against known speakers. Same voice
    // measures ~1.0 and different voices ~0.1 on the fixture; 0.5 is the safe midpoint.
    private const float MatchThreshold = 0.5f;

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
        _manager = new SpeakerEmbeddingManager(_embed.Dim);
        AppLog.Info("diarizer: loaded seg=" + System.IO.Path.GetFileName(segmentationModel) + " emb=" + System.IO.Path.GetFileName(embeddingModel) + " threads=" + numThreads);
    }

    // Assigns the dominant speaker of one finalized 16 kHz mono turn as a stable spk_N id.
    // Empty audio carries no speaker: returns an Unknown hint rather than throwing, and
    // never invents an identity for a turn it cannot hear.
    public SpeakerHint AssignTurn(TurnAudio turn)
    {
        if (turn.Samples16kMono.IsEmpty) return Unknown;
        var samples = turn.Samples16kMono;
        var segments = _diar.Process(samples.ToArray());
        if (segments.Length == 0) return Unknown;

        var dominant = segments.OrderByDescending(s => s.End - s.Start).First().Speaker;
        var emb = EmbeddingFor(samples.Span, segments.Where(s => s.Speaker == dominant));

        var id = _manager.Search(emb, MatchThreshold);
        if (string.IsNullOrEmpty(id))
        {
            id = $"spk_{_next++}";
            _manager.Add(id, emb);
            AppLog.Debug("diarizer: new speaker " + id);
        }
        return new SpeakerHint(id, SpeakerOrigin.Embedding, null);
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

    private static readonly SpeakerHint Unknown = new("", SpeakerOrigin.Unknown, null);

    public void Dispose() { _manager.Dispose(); _embed.Dispose(); _diar.Dispose(); }
}


