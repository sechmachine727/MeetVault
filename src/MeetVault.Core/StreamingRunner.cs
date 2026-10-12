using System.Collections.Concurrent;

namespace MeetVault.Core;

// Shared threading + queue + event plumbing for streaming engines. Push() only enqueues
// (the capture pump must never block on the recognizer); a dedicated thread decodes,
// interim text is deduplicated, finals reset the utterance clock, and the bounded queue
// drops the OLDEST chunks (with a rate-limited warn) when the decoder falls behind.
public sealed class StreamingRunner : IDisposable
{
    private readonly IStreamingBackend _backend;
    private readonly Func<long> _nowMs;
    // Segment ids must be unique across meetings AND engines: transcript_segments.id is a
    // global PRIMARY KEY, and every runner instance restarts its own sequence at 0 — two
    // meetings would both mint "s0" and the second meeting's saves would die on the UNIQUE
    // constraint (hit live: the vosk test run could not save or stop at all).
    // The full GUID, not a short slice: 8 hex chars is only a 32-bit namespace, and a
    // collision on the global PRIMARY KEY fails the whole save.
    private readonly string _idPrefix = "s" + Guid.NewGuid().ToString("n");
    private BlockingCollection<(float[] Samples, long? CaptureWallMs)>? _queue;
    private Thread? _thread;
    private volatile bool _running;
    private int _droppedChunks;
    private int _utteranceSeq;
    private long _utteranceStartMs;
    private bool _inReplay;                 // processing replayed pre-roll audio (review r19)
    private long? _currentChunkWallMs;      // capture wall time of the chunk being processed
    private string _lastPartial = "";
    private bool _disposed;
    // How long FlushAndWait gives the decode thread to drain before reporting failure
    // (tests shrink it; production keeps the 5 s budget).
    private readonly int _joinTimeoutMs;

    public event Action<string>? PartialReady;
    public event Action<Segment>? SegmentReady;

    public int DroppedChunks => _droppedChunks;

    public StreamingRunner(IStreamingBackend backend, Func<long>? nowMs = null, int joinTimeoutMs = 5000)
    {
        _backend = backend;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _joinTimeoutMs = joinTimeoutMs;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StreamingRunner));
        _queue = new BlockingCollection<(float[], long?)>(boundedCapacity: 4096); // 4096 chunks covers the full 30 s pre-roll cap (3000 × 10 ms chunks) plus margin: a swap's replay burst must never trip drop-oldest, and live backpressure still drops oldest beyond that (~10 s of 10 ms chunks absorbs cold-start decode lag; measured: cold-start decodes can run far slower than real time)
        _lastPartial = "";
        _inReplay = false;
        _currentChunkWallMs = null;
        _utteranceStartMs = _nowMs();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "streaming-decode" };
        _thread.Start();
        AppLog.Info("streaming: decode thread started");
    }

    public void Push(float[] samples16k) => Enqueue(samples16k, null);

    // Pre-roll replay: these samples were captured BEFORE the engine existed; carry their
    // capture wall time so segment timestamps are backdated to when the audio actually
    // happened instead of being stamped at (much later) replay time (review r19).
    public void PushReplay(float[] samples16k, long captureWallMs) => Enqueue(samples16k, captureWallMs);

    private void Enqueue(float[] samples16k, long? captureWallMs)
    {
        var q = _queue;
        if (!_running || q is null) return;
        // A chunk can race Stop/pause: the _running read above is a fast path, not the
        // synchronization — FlushAndWait can complete (or Dispose can free) the queue
        // between that read and the calls below. A late chunk is a no-op, never an
        // exception: this runs on the capture pump, where a throw surfaces as
        // CaptureFailed and stops the recording.
        try
        {
            for (var attempts = 0; attempts < 9; attempts++)
            {
                if (q.TryAdd((samples16k, captureWallMs))) return;
                if (q.TryTake(out _))
                {
                    var n = System.Threading.Interlocked.Increment(ref _droppedChunks);
                    if (n == 1 || n % 50 == 0)
                        AppLog.Warn("streaming: decoder behind, dropped " + n + " chunk(s)");
                }
                // Else the consumer drained the queue between the failed add and the take:
                // retry the add — the NEWEST chunk is only ever dropped as a last resort,
                // counted, never silently (review r11).
            }
            var d = System.Threading.Interlocked.Increment(ref _droppedChunks);
            if (d == 1 || d % 50 == 0)
                AppLog.Warn("streaming: decoder behind, dropped " + d + " chunk(s)");
        }
        // InvalidOperationException = the queue was completed (Stop/pause) mid-call;
        // ObjectDisposedException is its subclass and covered by the same clause.
        catch (InvalidOperationException) { }
    }

    private void Loop()
    {
        try
        {
            foreach (var (chunk, captureWall) in _queue!.GetConsumingEnumerable())
            {
                _currentChunkWallMs = captureWall;
                if (captureWall is long cw)
                {
                    // First replayed chunk after live audio: the open utterance starts at
                    // its capture time, not the (late) replay wall clock (review r19).
                    if (!_inReplay) { _inReplay = true; _utteranceStartMs = cw; }
                }
                else if (_inReplay) _inReplay = false;
                _backend.AcceptWaveform(chunk);
                Emit();
            }
            Emit();
            if (_backend.TryFlushFinal(out var tail) && !string.IsNullOrWhiteSpace(tail)) Finalize(tail);
            _lastPartial = "";
            RaisePartial("");
        }
        catch (Exception ex)
        {
            AppLog.Error("streaming: decode loop failed", ex);
            // The consumer is gone: stop claiming running and complete the queue so
            // pause/stop/restart recover instead of the failure being masked (review r11).
            _running = false;
            try { _queue?.CompleteAdding(); } catch { }
        }
    }

    private void Emit()
    {
        if (_backend.TryTakeFinal(out var final))
        {
            if (!string.IsNullOrWhiteSpace(final)) Finalize(final);
            else { _lastPartial = ""; RaisePartial(""); }
            return;
        }
        var text = _backend.CurrentText();
        if (text.Length > 0 && text != _lastPartial)
        {
            _lastPartial = text;
            RaisePartial(text);
        }
    }

    private void Finalize(string text)
    {
        // Replayed chunks finalize on their CAPTURE clock; live chunks on the wall clock
        // (review r19).
        var now = _currentChunkWallMs ?? _nowMs();
        RaiseSegment(new Segment(_idPrefix + "-" + _utteranceSeq++, "", _utteranceStartMs, now, text, true));
        _utteranceStartMs = now;
        _lastPartial = "";
        RaisePartial("");
    }

    // A throwing subscriber must not tear down the decode loop: the pipeline would go
    // deaf for the rest of the meeting while IsRunning still reports true (review r19).
    private void RaisePartial(string text)
    {
        try { PartialReady?.Invoke(text); } catch (Exception ex) { AppLog.Warn("streaming: partial subscriber threw: " + ex.Message); }
    }

    private void RaiseSegment(Segment segment)
    {
        try { SegmentReady?.Invoke(segment); } catch (Exception ex) { AppLog.Warn("streaming: segment subscriber threw: " + ex.Message); }
    }

    // Stops accepting chunks and waits for the decode thread to drain (up to the join
    // timeout). Returns TRUE when the thread has exited (or never ran) and FALSE while it
    // is still alive — a stalled subscriber or a full backlog can outlive the wait — in
    // which case the caller must NOT dispose the backend or start a second decode thread
    // over it: the loop is still calling into the native recognizer.
    public bool FlushAndWait()
    {
        var q = _queue;
        _running = false;
        if (q is null) return true;
        try { q.CompleteAdding(); } catch (ObjectDisposedException) { return true; }
        var t = _thread;
        if (t is null || t == Thread.CurrentThread) return true;
        if (t.Join(TimeSpan.FromMilliseconds(_joinTimeoutMs))) return true;
        AppLog.Warn("streaming: decode thread still draining after " + _joinTimeoutMs + " ms; keeping the backend alive");
        return false;
    }

    public void Flush() => FlushAndWait();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var exited = true;
        try { exited = FlushAndWait(); } catch { }
        // The decode thread still owns the backend and the queue: freeing either under it
        // risks a native access violation. They are left to the background thread / GC.
        if (!exited) return;
        try { _backend.Dispose(); } catch { }
        _queue?.Dispose();
    }
}
