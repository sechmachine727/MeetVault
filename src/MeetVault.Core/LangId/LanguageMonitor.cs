using Microsoft.ML.OnnxRuntime;

namespace MeetVault.Core;

// Feeds the language-ID model from the capture pump without ever blocking it: Push()
// appends to a bounded buffer; one background thread runs energy-gated windows through
// the detector at a throttled rate and reports detections. Suggestions only flow when
// Enabled (auto mode) and the policy agrees on a different language.
public sealed class LanguageMonitor : IDisposable
{
    private readonly Func<float[], (string Code, float Confidence)?> _detect;
    private readonly AutoLanguagePolicy _policy;
    private readonly SpeechWindowGate _gate;
    private readonly int _minInferenceGapMs;
    private readonly object _lock = new();
    private readonly List<float> _pending = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private long _lastInferMs;

    public bool Enabled { get; set; } = true;                       // false in manual mode
    public string? ActiveLanguage { get; private set; }
    public event Action<string, float>? Detected;                   // every accepted detection (indicator)
    public event Action<string>? Suggested;                         // policy agreed (caller swaps at a boundary)

    public LanguageMonitor(Func<float[], (string, float)?> detect, AutoLanguagePolicy? policy = null, int minInferenceGapMs = 1200)
    {
        _detect = detect;
        _policy = policy ?? new AutoLanguagePolicy();
        _minInferenceGapMs = minInferenceGapMs;
        _gate = new SpeechWindowGate();
        _thread = new Thread(Loop) { IsBackground = true, Name = "langid-monitor" };
        _thread.Start();
    }

    public void SetActive(string? code) { lock (_lock) ActiveLanguage = code; _policy.SetActive(code); }

    public void Reset() { lock (_lock) _pending.Clear(); _gate.Reset(); }

    public void Push(float[] samples16k) { lock (_lock) _pending.AddRange(samples16k); _wake.Set(); }

    private void Loop()
    {
        while (!_stop)
        {
            _wake.WaitOne(500);
            float[] batch;
            lock (_lock)
            {
                // Never hold more than ~30 s of audio: a stalled detector must not grow forever.
                var max = 30 * 16 * 1000;
                if (_pending.Count > max) _pending.RemoveRange(0, _pending.Count - max);
                if (_pending.Count == 0) continue;
                batch = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var window in _gate.Push(batch))
            {
                var now = Environment.TickCount64;
                if (now - _lastInferMs < _minInferenceGapMs) continue;   // throttle: ≤1 inference per gap
                _lastInferMs = now;
                (string Code, float Confidence)? result;
                try { result = _detect(window); }
                catch (Exception ex) { AppLog.Warn("langid: detect failed: " + ex.Message); continue; }
                if (result is not { } r) continue;
                Detected?.Invoke(r.Code, r.Confidence);
                if (!Enabled) continue;
                var suggestion = _policy.Detection(r.Code, r.Confidence, now);
                if (suggestion is not null) Suggested?.Invoke(suggestion);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        try { _thread.Join(1500); } catch { }
        _wake.Dispose();
    }
}
