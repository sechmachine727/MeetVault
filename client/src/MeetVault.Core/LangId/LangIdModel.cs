using Microsoft.ML.OnnxRuntime;

namespace MeetVault.Core;

// CPU ONNX wrapper around the pinned VoxLingua107 ECAPA-TDNN language-ID export
// (models/langid/voxlingua107.onnx — repo christopherthompson81/voxlingua107-lid-onnx,
// revision e02e1da8, md5 90101ebfee08d96afe7d516e988910fa; see scratch/langid-probe/PIN.md).
// Pinned I/O (measured, not guessed): input `audio` [batch, samples] float32 — any clip
// length, 16 kHz mono PCM in [-1, 1], FBANK + normalization folded into the graph; output
// index 0 = `logits` [1,107] which is ALREADY log-softmax, so probability = exp(logit).
// One instance per process; Detect is called from the monitor thread, the lock keeps a
// stray second caller safe.
public sealed class LangIdModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _labels;          // class index -> model label (e.g. "en")
    private readonly object _lock = new();

    private LangIdModel(InferenceSession session, string[] labels)
    { _session = session; _labels = labels; }

    public static bool IsAvailable(string modelsRoot)
        => File.Exists(Path.Combine(modelsRoot, "langid", "voxlingua107.onnx"))
        && File.Exists(Path.Combine(modelsRoot, "langid", "lang_map.json"));

    public static LangIdModel Load(string modelsRoot)
    {
        var dir = Path.Combine(modelsRoot, "langid");
        // Parse the map BEFORE opening the 86 MB native session: a malformed hand-staged
        // lang_map.json must not leak the session (review r20).
        var labels = ParseLangMap(File.ReadAllText(Path.Combine(dir, "lang_map.json")));
        // The session copies the options, so the native options object is released here
        // instead of leaking one per Load (review r10).
        using var opts = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        var session = new InferenceSession(Path.Combine(dir, "voxlingua107.onnx"), opts);
        return new LangIdModel(session, labels);
    }

    // Pinned shape: a JSON object with keys "0".."106" -> {"iso": <2-letter>, "name": ...}.
    // Also tolerates a {"labels": [...]} array for future re-exports.
    internal static string[] ParseLangMap(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("labels", out var arr))
            return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        var map = new SortedDictionary<int, string>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (int.TryParse(p.Name, out var i))
                map[i] = p.Value.TryGetProperty("iso", out var iso) ? (iso.GetString() ?? "") : "";
        var result = new string[map.Count == 0 ? 0 : map.Keys.Max() + 1];
        foreach (var (i, l) in map) result[i] = l;
        return result;
    }

    // Top class mapped to an app language + probability, or null when the top class is
    // not a shipped transcription language.
    public (string Code, float Confidence)? Detect(float[] samples16k)
    {
        var top = TopK(samples16k, 1);
        if (top.Count == 0) return null;
        var code = LangIdLabels.ToAppCode(top[0].Label);
        return code is null ? null : (code, top[0].Probability);
    }

    public IReadOnlyList<(string Label, float Probability)> TopK(float[] samples16k, int k)
    {
        lock (_lock)
        {
            // Bind by the pinned names (review r12): a re-export with an extra or reordered
            // tensor must fail loudly instead of silently reading the wrong one.
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("audio",
                new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(samples16k, new[] { 1, samples16k.Length })) }, new[] { "logits" });
            var logits = results[0].AsEnumerable<float>().ToArray();   // pinned output: "logits"
            // Pinned output is log-softmax: probability = exp(logit). Normalize anyway so
            // the reported value is a true probability regardless of export numerics.
            var max = logits.Max();
            var exps = logits.Select(v => MathF.Exp(v - max)).ToArray();
            var sum = exps.Sum();
            return Enumerable.Range(0, logits.Length)
                .OrderByDescending(i => exps[i])
                .Take(k)
                .Select(i => (i < _labels.Length ? _labels[i] : "?", exps[i] / sum))
                .ToArray();
        }
    }

    public void Dispose()
    {
        // The monitor thread may be parked inside TopK's Run; dispose under the same lock
        // so an in-flight session is never freed underneath it (review r7).
        lock (_lock) _session.Dispose();
    }
}
