using System.Globalization;

namespace IDVBuff.Appearance;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Parse(string value) => TryParse(value, out var color)
        ? color : throw new FormatException($"Invalid opaque sRGB color: {value}");

    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    public double Luminance => .2126 * Linear(R) + .7152 * Linear(G) + .0722 * Linear(B);
    public static double Contrast(RgbColor a, RgbColor b) =>
        (Math.Max(a.Luminance, b.Luminance) + .05) / (Math.Min(a.Luminance, b.Luminance) + .05);

    public static RgbColor Mix(RgbColor a, RgbColor b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new(Byte(a.R + (b.R - a.R) * amount), Byte(a.G + (b.G - a.G) * amount), Byte(a.B + (b.B - a.B) * amount));
    }

    internal static double Linear(byte value)
    {
        var c = value / 255d;
        return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
    }

    internal static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
