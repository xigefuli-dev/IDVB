namespace IDVBuff.Survey.Domain;

public readonly record struct SurveyRasterPixel(byte R, byte G, byte B, byte A);

public readonly record struct SurveyCompositeLayerPixel(
    int ZOrder,
    bool IsVisible,
    bool IsDeleted,
    double Opacity,
    SurveyLayerTransform Transform,
    int PixelWidth,
    int PixelHeight,
    SurveyRasterPixel? Pixel);

public static class SurveyCompositePixelSampler
{
    public static SurveyRasterPixel? Composite(
        SurveyWorldPoint worldPoint,
        IEnumerable<SurveyCompositeLayerPixel> layers)
    {
        var red = 0d;
        var green = 0d;
        var blue = 0d;
        var alpha = 0d;
        foreach (var layer in layers
            .Where(item => item.IsVisible && !item.IsDeleted)
            .OrderByDescending(item => item.ZOrder))
        {
            if (layer.Pixel is not { } pixel
                || layer.PixelWidth <= 0
                || layer.PixelHeight <= 0
                || !layer.Transform.IsValid
                || !double.IsFinite(layer.Opacity)
                || layer.Opacity <= 0d)
                continue;

            var local = layer.Transform.InverseTransform(worldPoint);
            if (local.X < 0d || local.Y < 0d
                || local.X >= layer.PixelWidth
                || local.Y >= layer.PixelHeight)
                continue;

            var layerAlpha = (pixel.A / 255d) * Math.Clamp(layer.Opacity, 0d, 1d);
            if (layerAlpha <= double.Epsilon)
                continue;
            var remaining = 1d - alpha;
            red += pixel.R * layerAlpha * remaining;
            green += pixel.G * layerAlpha * remaining;
            blue += pixel.B * layerAlpha * remaining;
            alpha += layerAlpha * remaining;
            if (alpha >= 0.999999d)
                break;
        }

        if (alpha <= double.Epsilon)
            return null;
        return new SurveyRasterPixel(
            ToByte(red / alpha),
            ToByte(green / alpha),
            ToByte(blue / alpha),
            ToByte(alpha * 255d));
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value), 0d, 255d);
}
