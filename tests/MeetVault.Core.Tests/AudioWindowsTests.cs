using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The splitter exists because the moonshine v2 (2026-02-27) quantized exports fail
// beyond ~9.2 s per decode (ORT broadcast error -> empty result; measured on tiny-en
// and tiny-ja). These tests pin the coverage and the quiet-point choice without models.
public sealed class AudioWindowsTests
{
    [Fact]
    public void Short_ReturnsSingleWindow()
    {
        var w = AudioWindows.Split(new float[5 * 16000], 8000);
        Assert.Single(w);
        Assert.Equal(0, w[0].OffsetSamples);
        Assert.Equal(5 * 16000, w[0].LengthSamples);
    }

    [Fact]
    public void Long_CutsAtTheQuietestFrame_BeforeTheLimit()
    {
        // 10 s of constant speech-level audio with a 100 ms silent hole at 6.6 s.
        var s = new float[10 * 16000];
        Array.Fill(s, 0.2f);
        Array.Clear(s, 105600, 1600);

        var w = AudioWindows.Split(s, 8000);

        Assert.Equal(2, w.Count);
        // Quietest 50 ms frame in [6.5 s, 8 s) is [105600, 106400); the cut is its center.
        Assert.Equal(106000, w[0].LengthSamples);
        Assert.Equal(106000, w[1].OffsetSamples);
        Assert.Equal(160000 - 106000, w[1].LengthSamples);
    }

    [Fact]
    public void UniformAudio_StillCoversEverything_Contiguously()
    {
        var s = new float[30 * 16000];
        Array.Fill(s, 0.1f);

        var w = AudioWindows.Split(s, 8000);

        Assert.True(w.Count >= 4, "a 30 s clip must take several bounded windows");
        var expected = 0;
        foreach (var win in w)
        {
            Assert.True(win.LengthSamples > 0);
            Assert.True(win.LengthSamples <= 8000 * 16, "no window may exceed the limit");
            Assert.Equal(expected, win.OffsetSamples);
            expected += win.LengthSamples;
        }
        Assert.Equal(30 * 16000, expected);
    }
}
