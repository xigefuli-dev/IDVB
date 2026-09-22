namespace IDVBuff.Appearance;

public sealed record AccentPalette(RgbColor Fill, RgbColor Hover, RgbColor Pressed,
    RgbColor OnFill, RgbColor Text, RgbColor Selection, RgbColor SelectionHover,
    RgbColor OnSelection, RgbColor Border);

public static class AccentPaletteGenerator
{
    private static readonly RgbColor Black = new(0, 0, 0);
    private static readonly RgbColor White = new(255, 255, 255);

    public static AccentPalette Generate(RgbColor seed, bool dark, RgbColor surface)
    {
        var fill = EnsureContrast(seed, surface, 4.5, dark);
        var onFill = RgbColor.Contrast(fill, Black) > RgbColor.Contrast(fill, White) ? Black : White;
        var hover = EnsureContrast(ShiftLightness(fill, dark ? -.035 : .035), onFill, 4.5, onFill == Black);
        var pressed = EnsureContrast(ShiftLightness(fill, dark ? -.07 : .07), onFill, 4.5, onFill == Black);
        var selection = RgbColor.Mix(surface, fill, dark ? .20 : .12);
        var selectionHover = RgbColor.Mix(surface, fill, dark ? .27 : .18);
        var text = EnsureContrast(fill, selectionHover, 4.5, dark);
        var border = EnsureContrast(fill, selectionHover, 3, dark);
        return new(fill, hover, pressed, onFill, fill, selection, selectionHover, text, border);
    }

    public static RgbColor EnsureContrast(RgbColor color, RgbColor background, double minimum, bool lighter)
    {
        if (RgbColor.Contrast(color, background) >= minimum)
            return color;
        var lab = ToOklab(color);
        for (var i = 1; i <= 100; i++)
        {
            var target = lighter ? 1d : 0d;
            var candidate = FromOklab(lab.L + (target - lab.L) * i / 100d, lab.A, lab.B);
            if (RgbColor.Contrast(candidate, background) >= minimum)
                return candidate;
        }
        throw new ArgumentException("无法生成满足对比度要求的主题颜色。");
    }

    private static RgbColor ShiftLightness(RgbColor color, double delta)
    {
        var lab = ToOklab(color);
        return FromOklab(Math.Clamp(lab.L + delta, 0, 1), lab.A, lab.B);
    }

    private static (double L, double A, double B) ToOklab(RgbColor color)
    {
        var r = RgbColor.Linear(color.R); var g = RgbColor.Linear(color.G); var b = RgbColor.Linear(color.B);
        var l = Math.Cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
        var m = Math.Cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
        var s = Math.Cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
        return (.2104542553 * l + .7936177850 * m - .0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + .4505937099 * s,
            .0259040371 * l + .7827717662 * m - .8086757660 * s);
    }

    private static RgbColor FromOklab(double lightness, double a, double b)
    {
        // Reduce chroma without changing the hue direction until the color is in sRGB.
        for (var step = 100; step >= 0; step--)
        {
            var chroma = step / 100d;
            var l = Math.Pow(lightness + .3963377774 * a * chroma + .2158037573 * b * chroma, 3);
            var m = Math.Pow(lightness - .1055613458 * a * chroma - .0638541728 * b * chroma, 3);
            var s = Math.Pow(lightness - .0894841775 * a * chroma - 1.2914855480 * b * chroma, 3);
            var r = 4.0767416621 * l - 3.3077115913 * m + .2309699292 * s;
            var g = -1.2684380046 * l + 2.6097574011 * m - .3413193965 * s;
            var blue = -.0041960863 * l - .7034186147 * m + 1.7076147010 * s;
            if (r < -.000001 || g < -.000001 || blue < -.000001 || r > 1.000001 || g > 1.000001 || blue > 1.000001)
                continue;
            return new(Encode(r), Encode(g), Encode(blue));
        }
        throw new InvalidOperationException("sRGB gamut mapping failed.");
    }

    private static byte Encode(double value) => RgbColor.Byte(255 * (value <= .0031308
        ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055));
}
