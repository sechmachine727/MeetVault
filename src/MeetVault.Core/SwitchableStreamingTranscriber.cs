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
    private const int PreRollMs = 6000;
    private IStreamingTranscriber? _inner;
    private string? _activeModelDir;

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
            replay = _preRoll.ToArray();
            _preRoll.Clear();
        }
        if (inner is not null)
        {
            // Observed: a failed start must surface to the caller, not die as an unobserved
            // task exception while the relay claims running (review r6).
            await inner.StartAsync(ct).ConfigureAwait(false);
            foreach (var chunk in replay) inner.Push(chunk);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        IStreamingTranscriber? inner;
        lock (_swapLock) { IsRunning = false; inner = _inner; }
        inner?.StopAsync(ct);
        return Task.CompletedTask;
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

    public void Flush() { IStreamingTranscriber? inner; lock (_swapLock) inner = _inner; inner?.Flush(); }

    // Swap the ACTIVE engine. Returns false when the replacement could not be built or
    // started; in that case the OLD engine keeps serving the meeting (review r7: the old
    // code detached and disposed first, so a factory failure left the relay deaf once the
    // pre-roll filled). A semaphore serializes swaps so two callers cannot build two
    // engines and leak the loser. Call off the UI thread: the factory loads a model.
    public bool SwapTo(string modelDir)
    {
        _swapOne.Wait();
        try
        {
            lock (_swapLock) { if (modelDir == _activeModelDir) return true; }

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
            IStreamingTranscriber? old;
            lock (_swapLock)
            {
                // Publish + pre-roll replay are atomic with Push (which takes this lock),
                // so the live stream keeps its order.
                old = _inner;
                _inner = fresh;
                _activeModelDir = modelDir;
                if (start)
                {
                    foreach (var chunk in _preRoll) fresh.Push(chunk);
                    _preRoll.Clear();
                }
            }
            if (old is not null)
            {
                // Flush BEFORE detaching: the old engine's last sentence still reaches the
                // session through this relay. It is already detached, so no late events.
                try { old.Flush(); } catch (Exception ex) { AppLog.Warn("swap: flush failed: " + ex.Message); }
                old.PartialReady -= ForwardPartial;
                old.SegmentReady -= ForwardSegment;
                try { old.Dispose(); } catch { }
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
        // Serialize against an in-flight SwapTo (review r8): its Task.Run can be loading a
        // model while the page unloads; without this, the swap would publish and start a
        // fresh engine into a disposed relay — a leaked model with a parked decode thread.
        _swapOne.Wait();
        try
        {
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
        finally { _swapOne.Release(); }
    }
}
