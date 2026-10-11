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
    // Gate + policy are monitor-thread territory (Push/Detection); Reset and SetActive run
    // on the UI thread mid-drain, so they take this lock too (review r6).
    private readonly object _gateLock = new();
    private readonly List<float> _pending = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private long _lastInferMs;

    public volatile bool Enabled = true;                            // false in manual mode; UI-written, monitor-read
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

    public void SetActive(string? code) { lock (_gateLock) _policy.SetActive(code); }

    // For caller-applied AUTO switches: keeps the anti-thrash cooldown through the echo
    // (review r6).
    public void SetActiveKeepCooldown(string? code) { lock (_gateLock) _policy.SetActiveKeepCooldown(code); }

    public void Reset()
    {
        lock (_lock) _pending.Clear();
        lock (_gateLock) { _gate.Reset(); _policy.ResetAgreement(); }
    }

    public void Push(float[] samples16k)
    {
        if (_stop) return; // fast path; the catch covers the check-then-act window (review r9)
        lock (_lock) _pending.AddRange(samples16k);
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
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
            float[][] windows;
            lock (_gateLock) windows = _gate.Push(batch).ToArray();
            foreach (var window in windows)
            {
                var now = Environment.TickCount64;
                if (now - _lastInferMs < _minInferenceGapMs) continue;   // throttle: ≤1 inference per gap
                if (!Enabled) continue;
                // A manual language disables auto: skip the inference entirely — the 86 MB
                // detector must not keep running for the page lifetime (review r12).
                _lastInferMs = now;
                (string Code, float Confidence)? result;
                try { result = _detect(window); }
                catch (Exception ex) { AppLog.Warn("langid: detect failed: " + ex.Message); continue; }
                if (result is not { } r) continue;
                Detected?.Invoke(r.Code, r.Confidence);
                string? suggestion;
                lock (_gateLock) suggestion = _policy.Detection(r.Code, r.Confidence, now);
                if (suggestion is not null) Suggested?.Invoke(suggestion);
            }
            }
            catch (Exception ex) { AppLog.Warn("langid: monitor iteration failed: " + ex.Message); Thread.Sleep(250); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        // Join can time out on a wedged decode; disposing the handle while the loop may
        // still be parked on it would surface ObjectDisposedException on that thread, so
        // the handle is deliberately leaked in that case (review r5).
        if (!_thread.Join(1500)) { AppLog.Warn("langid: monitor thread did not exit; leaving the wake handle undisposed"); return; }
        _wake.Dispose();
    }
}
