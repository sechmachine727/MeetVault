using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class Pcm16Tests
{
    [Fact]
    public void RoundTrip_PreservesSignal()
    {
        var floats = new[] { 0f, 0.5f, -0.5f, 0.9999f, -0.9999f };
        var bytes = Pcm16.FromFloats(floats);
        var back = Pcm16.ToFloats(bytes);
        Assert.Equal(floats.Length, back.Length);
        for (var i = 0; i < floats.Length; i++)
            // 16-bit codec worst case is ~4.6e-5 (half-step 1/21845 at full scale); 1/16384 leaves headroom.
            Assert.True(Math.Abs(floats[i] - back[i]) < 1f / 16384f, $"sample {i}: {floats[i]} vs {back[i]}");
    }

    [Fact]
    public void ToFloats_Empty_ReturnsEmpty() => Assert.Empty(Pcm16.ToFloats(Array.Empty<byte>()));
}
