using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The CUDA gate must match the architectures the shipped ggml-cuda native actually carries.
// It is SASS-only (no PTX) for sm_86, sm_89, sm_120a and sm_121a, and a cubin runs on later
// minors of its own major. Getting this wrong is a false capability claim: the picker offers
// CUDA and the app silently falls back to CPU, which is the exact failure the gate exists to
// prevent. The set is frozen here so a whisper.net bump that changes it fails loudly.
public sealed class CudaCapabilityTests
{
    [Theory]
    [InlineData(50, false)]  // Maxwell
    [InlineData(61, false)]  // Pascal (GTX 10 series)
    [InlineData(75, false)]  // Turing (RTX 20 series, T4)
    [InlineData(80, false)]  // A100
    [InlineData(86, true)]   // Ampere consumer (RTX 30 series)
    [InlineData(87, true)]   // Orin, via the sm_86 cubin
    [InlineData(89, true)]   // Ada (RTX 40 series)
    [InlineData(90, false)]  // Hopper H100: newer than 8.6 but no code shipped
    [InlineData(100, false)] // Blackwell datacenter
    [InlineData(120, true)]  // Blackwell consumer (RTX 50 series)
    [InlineData(121, true)]  // Blackwell variant
    [InlineData(122, false)] // future minor: deliberately not advertised
    [InlineData(130, false)] // future major: deliberately not advertised
    public void CapabilitySupported_MatchesTheShippedCubins(int capability, bool expected)
    {
        Assert.Equal(expected, GpuProbe.CapabilitySupported(capability));
    }
}

