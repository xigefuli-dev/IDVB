namespace IDVBuff.Appearance;

public static class ThemeColorTransition
{
    // Correct only a foreground that actually loses contrast during interpolation.
    // A bounded sRGB search avoids palette generation/Oklab work on rendering frames.
    public static RgbColor KeepReadable(RgbColor foreground, RgbColor background,
        bool lightForeground, double minimum = 4.5)
    {
        if (RgbColor.Contrast(foreground, background) >= minimum) return foreground;
        var end = lightForeground ? new RgbColor(255, 255, 255) : new RgbColor(0, 0, 0);
        if (RgbColor.Contrast(end, background) < minimum)
            end = lightForeground ? new(0, 0, 0) : new(255, 255, 255);
        var low = 0d;
        var high = 1d;
        for (var i = 0; i < 12; i++)
        {
            var middle = (low + high) / 2;
            if (RgbColor.Contrast(RgbColor.Mix(foreground, end, middle), background) >= minimum)
                high = middle;
            else low = middle;
        }
        return RgbColor.Mix(foreground, end, high);
    }
}
