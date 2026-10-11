namespace MeetVault.Core;

// Splits one VAD utterance into fixed-limit decode windows for engines whose model
// export caps the input length (moonshine v2 2026-02-27 quantized exports fail beyond
// ~9.2 s per decode — ORT broadcast error in the attention Add, empty result; measured
// on tiny-en and tiny-ja). Each cut targets the quietest 50 ms frame in the lookback
// window before the limit so words are not sliced mid-syllable. Pure: no IO, no state.
public static class AudioWindows
{
    public readonly record struct Window(int OffsetSamples, int LengthSamples);

    // 16 kHz mono: 16 samples per ms.
    public static List<Window> Split(float[] samples16k, int maxMs, int lookbackMs = 1500, int frameMs = 50)
    {
        var result = new List<Window>();
        var maxSamples = maxMs * 16;
        var lookSamples = lookbackMs * 16;
        var frameSamples = frameMs * 16;
        var offset = 0;
        while (offset < samples16k.Length)
        {
            if (samples16k.Length - offset <= maxSamples)
            {
                result.Add(new Window(offset, samples16k.Length - offset));
                break;
            }
            var cut = offset + maxSamples; // the latest allowed end for this window
            var searchStart = Math.Max(offset, cut - lookSamples);
            var bestStart = Math.Max(searchStart, cut - frameSamples); // fallback: hug the limit
            double bestEnergy = double.MaxValue;
            for (var s = searchStart; s + frameSamples <= cut; s += frameSamples)
            {
                double e = 0;
                for (var i = 0; i < frameSamples; i++) e += samples16k[s + i] * (double)samples16k[s + i];
                if (e < bestEnergy) { bestEnergy = e; bestStart = s; }
            }
            var end = bestStart + frameSamples / 2; // the center of the quiet frame
            result.Add(new Window(offset, end - offset));
            offset = end;
        }
        return result;
    }
}
