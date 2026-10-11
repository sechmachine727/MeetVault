namespace MeetVault.Core;

// One stable transcription endpoint for a meeting while the ACTIVE engine underneath can
// be replaced (auto/manual language switch). The session subscribes ONCE to this relay,
// so a swap never touches MeetingSession. Pre-roll: audio pushed before the first engine
// exists (the auto-detect "listen first" window) is buffered — up to PreRollMs — and
// replayed into the new engine so the first sentences are not lost.
//
// Swap safety: the old engine is flushed (its final utterance still reaches subscribers)
// and detached BEFORE disposal; the runner's contract (FlushAndWait/Dispose never free
// the backend under a live decode thread) makes the dispose safe even while it drains.
// Push only ever buffers or forwards — it never blocks on a swap's flush.
public sealed class SwitchableStreamingTranscriber : IStreamingTranscriber, ISpeechTranscriber, IDisposable
{
    private readonly Func<string, IStreamingTranscriber> _factory;   // modelDir -> engine
    private readonly object _swapLock = new();
    private readonly SemaphoreSlim _swapOne = new(1, 1);              // one swap at a time
    private readonly List<float[]> _preRoll = new();
    private const int PreRollMs = 30_000; // must cover the 6.5 s detect wait + a large-tier load (~16.5 s) — the first engine publishes only after both (review r17)
    private IStreamingTranscriber? _inner;
    private string? _activeModelDir;
    private volatile bool _disposed;                                 // set synchronously in Dispose; read by SwapTo
    private bool _coreDone;                                          // guarded by _swapOne

    public SwitchableStreamingTranscriber(Func<string, IStreamingTranscriber> factory)
    { _factory = factory; }

    public string? ActiveModelDir => _activeModelDir;
    public bool IsRunning { get; private set; }
    public event Action<Segment>? SegmentReady;
    public event Action<string>? PartialReady;

    public async Task StartAsync(CancellationToken ct)
    {
        IStreamingTranscriber? inner;
        float[][] replay;
        lock (_swapLock)
        {
            IsRunning = true;
            inner = _inner;
            // No engine yet (the auto "listen first" window): keep the pre-roll for the
            // first swap to replay — clearing it here dropped up to 6 s of captured speech
            // on an Auto-mode pause/resume (review r10).
            replay = inner is null ? Array.Empty<float[]>() : _preRoll.ToArray();
            if (inner is not null) _preRoll.Clear();
        }
        if (inner is not null)
        {
            // Observed: a failed start must surface to the caller, not die as an unobserved
            // task exception while the relay claims running (review r6).
            await inner.StartAsync(ct).ConfigureAwait(false);
            foreach (var chunk in replay) inner.Push(chunk);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        IStreamingTranscriber? inner;
        lock (_swapLock) { IsRunning = false; inner = _inner; }
        // Await the engine's stop: its flush joins the decode thread and delivers the final
        // through this relay BEFORE the caller's next step, so the pause latch ordering is
        // enforced instead of inferred from the dispatcher queue (review r10).
        if (inner is not null) await inner.StopAsync(ct).ConfigureAwait(false);
    }

    public void Push(float[] samples16k)
    {
        IStreamingTranscriber? inner;
        lock (_swapLock)
        {
            inner = _inner;
            if (inner is null)
            {
                // No engine yet (auto mode is still listening), or a swap is mid-flight:
                // keep a bounded pre-roll so no speech is lost.
                _preRoll.Add(samples16k);
                var max = PreRollMs * 16;
                var total = 0;
                foreach (var chunk in _preRoll) total += chunk.Length;
                while (total > max && _preRoll.Count > 1)
                {
                    total -= _preRoll[0].Length;
                    _preRoll.RemoveAt(0);
                }
                return;
            }
        }
        inner.Push(samples16k);
    }

    // Swap the ACTIVE engine. Returns false when the replacement could not be built or
    // started; in that case the OLD engine keeps serving the meeting (review r7: the old
    // code detached and disposed first, so a factory failure left the relay deaf once the
    // pre-roll filled). A semaphore serializes swaps so two callers cannot build two
    // engines and leak the loser. Call off the UI thread: the factory loads a model.
    // onlyIfNoEngine: re-asserted INSIDE the gate — a fallback that waited behind another
    // swap must not overwrite the engine that swap just published (review r14).
    public bool SwapTo(string modelDir, bool onlyIfNoEngine = false)
    {
        _swapOne.Wait();
        try
        {
            if (_disposed) return false; // a swap starting after Dispose must not build an engine (review r9)
            lock (_swapLock)
            {
                if (onlyIfNoEngine && _inner is not null) return false;
                if (modelDir == _activeModelDir) return true;
            }

            // Build the replacement BEFORE touching the live engine: a failure must leave
            // the meeting transcribing on what it already had.
            IStreamingTranscriber fresh;
            try { fresh = _factory(modelDir); }
            catch (Exception ex)
            {
                AppLog.Error("swap: creating engine failed for " + modelDir, ex);
                return false;
            }
            fresh.PartialReady += ForwardPartial;
            fresh.SegmentReady += ForwardSegment;
            bool start;
            lock (_swapLock) start = IsRunning;
            if (start)
            {
                try { fresh.StartAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    AppLog.Error("swap: starting engine failed for " + modelDir, ex);
                    fresh.PartialReady -= ForwardPartial;
                    fresh.SegmentReady -= ForwardSegment;
                    try { fresh.Dispose(); } catch { }
                    return false;   // the old engine keeps serving
                }
            }
            // Detach FIRST so pushes buffer into the pre-roll, then flush the old engine:
            // Flush joins its decode thread, so its final is delivered through the
            // still-live subscription BEFORE the fresh engine is published — no inverted
            // or overlapping segments (review r9). Unsubscribe only after the join.
            IStreamingTranscriber? old;
            lock (_swapLock) { old = _inner; _inner = null; }
            if (old is not null)
            {
                // Stop (flush + drain) BEFORE detaching: the old engine's last sentence
                // still reaches the session through this relay (review r9). Terminal for the
                // old engine, which is disposed right after (review r17).
                try { old.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { AppLog.Warn("swap: flush failed: " + ex.Message); }
                old.PartialReady -= ForwardPartial;
                old.SegmentReady -= ForwardSegment;
                try { old.Dispose(); } catch { }
            }
            lock (_swapLock)
            {
                // Publish + pre-roll replay are atomic with Push (which takes this lock),
                // so the live stream keeps its order.
                _inner = fresh;
                _activeModelDir = modelDir;
                if (start)
                {
                    foreach (var chunk in _preRoll) fresh.Push(chunk);
                    _preRoll.Clear();
                }
            }
            AppLog.Info("swap: active engine -> " + modelDir);
            return true;
        }
        finally { _swapOne.Release(); }
    }

    private void ForwardPartial(string text) => PartialReady?.Invoke(text);
    private void ForwardSegment(Segment segment) => SegmentReady?.Invoke(segment);

    public void Dispose()
    {
        // Set synchronously: a swap queued behind an in-flight one must refuse IMMEDIATELY
        // (review r10 — the deferred core used to set the flag only after winning the gate).
        _disposed = true;
        // Bounded wait: OnUnloaded runs this on the UI thread while a language switch may
        // be mid-model-load (~16.5 s large tier) — never block the UI for that (review r9).
        // If the swap is still in flight, the teardown is handed to the pool; the in-flight
        // swap completes first either way.
        if (_swapOne.Wait(TimeSpan.FromSeconds(2)))
        {
            try { DisposeCore(); } finally { _swapOne.Release(); }
            return;
        }
        _ = Task.Run(() =>
        {
            _swapOne.Wait();
            try { DisposeCore(); } finally { _swapOne.Release(); }
        });
    }

    private void DisposeCore()
    {
        if (_coreDone) return;
        _coreDone = true;
        IStreamingTranscriber? inner;
        lock (_swapLock)
        {
            inner = _inner;
            _inner = null;
            _preRoll.Clear();
            IsRunning = false;
        }
        if (inner is null) return;
        inner.PartialReady -= ForwardPartial;
        inner.SegmentReady -= ForwardSegment;
        try { inner.Dispose(); } catch { }
    }
}
