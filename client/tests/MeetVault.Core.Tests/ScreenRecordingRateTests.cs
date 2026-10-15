using MeetVault.Core;
using Xunit;

namespace MeetVault.Core.Tests;

// Owner requirement: the screen recording runs at 30 fps. The number is a named constant (one
// source of truth where the encoder options are built) and this pin keeps a silent edit from
// changing the shipped recording rate; the real proof of a run is the FILE's cadence
// (ffprobe: avg_frame_rate=30/1 and 33.33 ms inter-frame deltas - see the adapter's comment).
public class ScreenRecordingRateTests
{
    [Fact]
    public void The_screen_encoder_is_pinned_to_30fps()
    {
        Assert.Equal(30, ScreenRecorderLibAdapter.FramesPerSecond);
        Assert.Equal(6_000_000, ScreenRecorderLibAdapter.VideoBitsPerSecond);
    }
}
