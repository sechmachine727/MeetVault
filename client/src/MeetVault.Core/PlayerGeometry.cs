namespace MeetVault.Core;

// Video-area aspect modes. 16:9 is the default; Fit uses the video's own ratio once known.
public enum PlayerAspect { Ratio16x9, Ratio4x3, Fit }

public static class PlayerGeometry
{
    public static PlayerAspect Next(PlayerAspect aspect) => aspect switch
    {
        PlayerAspect.Ratio16x9 => PlayerAspect.Ratio4x3,
        PlayerAspect.Ratio4x3 => PlayerAspect.Fit,
        _ => PlayerAspect.Ratio16x9,
    };

    // Element height for the given width; null = let the element size naturally.
    // The result is always capped at maxHeight when a height is returned.
    public static double? HeightFor(
        double widthDip,
        PlayerAspect aspect,
        double maxHeight,
        double naturalWidth = 0,
        double naturalHeight = 0)
    {
        if (widthDip <= 0) return null;
        double ratio = aspect switch
        {
            PlayerAspect.Ratio16x9 => 9.0 / 16.0,
            PlayerAspect.Ratio4x3 => 3.0 / 4.0,
            _ => naturalWidth > 0 && naturalHeight > 0 ? naturalHeight / naturalWidth : 9.0 / 16.0,
        };
        return Math.Min(widthDip * ratio, maxHeight);
    }
}
