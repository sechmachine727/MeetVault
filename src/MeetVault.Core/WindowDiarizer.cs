namespace MeetVault.Core;

// Bridges window-level diarization to the segment-level ISpeakerDiarizer contract.
// The interface carries no audio, so the caller feeds each finalized 16 kHz mono
// window via AssignWindowSamples first; AssignTurn then returns that window's label.
// Lives in Core (not UI) so production and tests share one adapter.
public sealed class WindowDiarizer : ISpeakerDiarizer, IDisposable
{
    private readonly SherpaDiarizer _inner;
    // Written on the capture pump thread, read on the UI thread.
    private volatile string _current = "spk_1";
    public WindowDiarizer(SherpaDiarizer inner) { _inner = inner; }
    public void Dispose() => _inner.Dispose();
    public void AssignWindowSamples(byte[] pcm16)
    {
        try
        {
            var f = new float[pcm16.Length / 2];
            for (var i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(pcm16, 2 * i) / 32768f;
            _current = _inner.AssignWindow(f);
        }
        catch { }
    }
    public string AssignTurn(Segment segment) => _current;
}
