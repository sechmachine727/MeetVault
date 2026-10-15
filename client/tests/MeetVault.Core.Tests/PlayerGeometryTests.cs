namespace MeetVault.Core.Tests;

public class PlayerGeometryTests
{
    [Theory]
    [InlineData(PlayerAspect.Ratio16x9, 800, 450)]
    [InlineData(PlayerAspect.Ratio4x3, 800, 600)]
    public void HeightFor_Ratios(PlayerAspect aspect, double width, double expected)
        => Assert.Equal(expected, PlayerGeometry.HeightFor(width, aspect, 1000));

    [Fact]
    public void HeightFor_CapsAtMax()
        => Assert.Equal(540.0, PlayerGeometry.HeightFor(2000, PlayerAspect.Ratio16x9, 540));

    [Fact]
    public void HeightFor_Fit_UsesNaturalRatio()
        => Assert.Equal(337.5, PlayerGeometry.HeightFor(600, PlayerAspect.Fit, 540, naturalWidth: 1920, naturalHeight: 1080));

    [Fact]
    public void HeightFor_Fit_WithoutNatural_FallsBackTo16x9()
        => Assert.Equal(337.5, PlayerGeometry.HeightFor(600, PlayerAspect.Fit, 540));

    [Fact]
    public void HeightFor_ZeroWidth_IsNull()
        => Assert.Null(PlayerGeometry.HeightFor(0, PlayerAspect.Ratio16x9, 540));

    [Fact]
    public void Next_Cycles()
    {
        Assert.Equal(PlayerAspect.Ratio4x3, PlayerGeometry.Next(PlayerAspect.Ratio16x9));
        Assert.Equal(PlayerAspect.Fit, PlayerGeometry.Next(PlayerAspect.Ratio4x3));
        Assert.Equal(PlayerAspect.Ratio16x9, PlayerGeometry.Next(PlayerAspect.Fit));
    }
}
