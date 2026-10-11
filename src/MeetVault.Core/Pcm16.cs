namespace MeetVault.Core;

// PCM16 little-endian <-> normalized float conversion; the capture chain emits PCM16
// bytes while the recognizers speak floats. Lives in Core so the live page
// and tests share one implementation.
public static class Pcm16
{
    public static float[] ToFloats(byte[] pcm16)
    {
        var f = new float[pcm16.Length / 2];
        for (var i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(pcm16, 2 * i) / 32768f;
        return f;
    }

    public static byte[] FromFloats(float[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(b.AsSpan(2 * i), (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue));
        return b;
    }
}
