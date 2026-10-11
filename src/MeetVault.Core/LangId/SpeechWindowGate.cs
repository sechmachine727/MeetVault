namespace MeetVault.Core;

// Turns the raw 16 kHz stream into language-ID analysis windows. Only "voiced" audio
// counts, so silence, keyboard clicks and room noise never reach the detector: a window
// is emitted when a full window of audio is available and at least minVoicedMs of it is
// above the RMS gate (short pauses inside speech are fine). The stride is a quarter of
// the window, so a language turn is caught within ~1 s of ongoing speech. The buffer is
// bounded by construction: the drain loop runs while a full window is held, so at most
// one window of audio is kept between pushes.
public sealed class SpeechWindowGate
{
    private readonly float _rmsThreshold;
    private readonly int _windowMs, _strideMs, _minVoicedMs;
    private const int FrameMs = 20;
    private readonly List<float> _buffer = new();

    public SpeechWindowGate(float rmsThreshold = 0.008f, int windowMs = 4000, int strideMs = 1000, int minVoicedMs = 2500)
    { _rmsThreshold = rmsThreshold; _windowMs = windowMs; _strideMs = strideMs; _minVoicedMs = minVoicedMs; }

    public static float Rms(ReadOnlySpan<float> s)
    { double acc = 0; foreach (var v in s) acc += v * v; return (float)Math.Sqrt(acc / Math.Max(1, s.Length)); }

    // Push a chunk (any length); returns the complete windows that carried enough speech,
    // oldest first. Silent windows are dropped, never emitted.
    public List<float[]> Push(float[] samples16k)
    {
        _buffer.AddRange(samples16k);
        var windows = new List<float[]>();
        var windowSamples = _windowMs * 16;   // 16 samples per ms at 16 kHz
        while (_buffer.Count >= windowSamples)
        {
            var win = new float[windowSamples];
            _buffer.CopyTo(0, win, 0, windowSamples);
            if (VoicedMs(win) >= _minVoicedMs) windows.Add(win);
            _buffer.RemoveRange(0, _strideMs * 16);
        }
        return windows;
    }

    public int VoicedMs(float[] win)
    {
        var frame = FrameMs * 16;
        var voiced = 0;
        for (var i = 0; i + frame <= win.Length; i += frame)
            if (Rms(win.AsSpan(i, frame)) >= _rmsThreshold) voiced += FrameMs;
        return voiced;
    }

    public void Reset() => _buffer.Clear();
}
