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
    private BlockingCollection<float[]>? _queue;
    private Thread? _thread;
    private volatile bool _running;
    private int _droppedChunks;
    private int _utteranceSeq;
    private long _utteranceStartMs;
    private string _lastPartial = "";
    private bool _disposed;

    public event Action<string>? PartialReady;
    public event Action<Segment>? SegmentReady;

    public int DroppedChunks => _droppedChunks;

    public StreamingRunner(IStreamingBackend backend, Func<long>? nowMs = null)
    {
        _backend = backend;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StreamingRunner));
        _queue = new BlockingCollection<float[]>(boundedCapacity: 64); // ~640 ms of 10 ms chunks
        _lastPartial = "";
        _utteranceStartMs = _nowMs();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "streaming-decode" };
        _thread.Start();
        AppLog.Info("streaming: decode thread started");
    }

    public void Push(float[] samples16k)
    {
        var q = _queue;
        if (!_running || q is null) return;
        while (!q.TryAdd(samples16k))
        {
            if (q.TryTake(out _))
            {
                var n = System.Threading.Interlocked.Increment(ref _droppedChunks);
                if (n == 1 || n % 50 == 0)
                    AppLog.Warn("streaming: decoder behind, dropped " + n + " chunk(s)");
            }
            else break;
        }
    }

    private void Loop()
    {
        try
        {
            foreach (var chunk in _queue!.GetConsumingEnumerable())
            {
                _backend.AcceptWaveform(chunk);
                while (_backend.IsReady()) _backend.Decode();
                Emit();
            }
            Emit();
            if (_backend.TryFlushFinal(out var tail) && !string.IsNullOrWhiteSpace(tail)) Finalize(tail);
            _lastPartial = "";
            PartialReady?.Invoke("");
        }
        catch (Exception ex) { AppLog.Error("streaming: decode loop failed", ex); }
    }

    private void Emit()
    {
        if (_backend.TryTakeFinal(out var final))
        {
            if (!string.IsNullOrWhiteSpace(final)) Finalize(final);
            else { _lastPartial = ""; PartialReady?.Invoke(""); }
            return;
        }
        var text = _backend.CurrentText();
        if (text.Length > 0 && text != _lastPartial)
        {
            _lastPartial = text;
            PartialReady?.Invoke(text);
        }
    }

    private void Finalize(string text)
    {
        var now = _nowMs();
        SegmentReady?.Invoke(new Segment("s" + _utteranceSeq++, "", _utteranceStartMs, now, text, true));
        _utteranceStartMs = now;
        _lastPartial = "";
        PartialReady?.Invoke("");
    }

    public void Flush()
    {
        var q = _queue;
        if (!_running || q is null) return;
        _running = false;
        q.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Flush(); } catch { }
        try { _backend.Dispose(); } catch { }
        _queue?.Dispose();
    }
}
