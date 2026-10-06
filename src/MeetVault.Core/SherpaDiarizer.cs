using SherpaOnnx;

namespace MeetVault.Core;

// Real diarization behind ISpeakerDiarizer: sherpa-onnx 1.13.8 offline diarization (finalized windows).
// ponytail: window-level assignment; per-frame live labels arrive with the streaming upgrade.
public sealed class SherpaDiarizer : ISpeakerDiarizer, IDisposable
{
    private readonly OfflineSpeakerDiarization _diar;
    private readonly Dictionary<int, string> _ids = new();

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
    }

    // Assigns the dominant speaker of one finalized 16 kHz mono window as a stable spk_N id.
    // ponytail: no audio by contract — callers must route window audio via AssignWindow first (LivePage WindowDiarizer does).
    public string AssignTurn(Segment segment) => AssignWindow(Array.Empty<float>());

    public string AssignWindow(float[] samples16kMono)
    {
        if (samples16kMono is null || samples16kMono.Length == 0) throw new ArgumentException("No audio in this window.", "samples16kMono");
        var segments = _diar.Process(samples16kMono);
        if (segments.Length == 0) return "spk_1";
        var dominant = segments.OrderByDescending(s => s.End - s.Start).First().Speaker;
        if (!_ids.TryGetValue(dominant, out var id)) { id = $"spk_{_ids.Count + 1}"; _ids[dominant] = id; }
        return id;
    }

    public void Dispose() => _diar.Dispose();
}


