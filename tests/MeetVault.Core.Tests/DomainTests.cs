using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Rename_KeepsId()
    {
        var s = new Speaker("spk_1", "Male 1", Gender.Male, 1);
        Assert.Equal("spk_1", s.Rename("Bob").Id);
        Assert.Equal("Bob", s.Rename("Bob").DisplayName);
    }

    [Fact]
    public void Edit_KeepsTimings_Reassign_MovesSpeaker()
    {
        var seg = new Segment("seg_1", "spk_1", 1000, 2500, "hello", true);
        Assert.Equal((1000L, 2500L), (seg.Edit("hi").StartMs, seg.Edit("hi").EndMs));
        Assert.Equal("spk_2", seg.Reassign("spk_2").SpeakerId);
    }
}

