using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Maps full-reference pixels to physical screen pixels. Runtime alignment
/// always uses one uniform scale and one fixed rotation.
/// </summary>
public sealed class MapSimilarityTransform
{
    public double Scale { get; init; } = 1d;
    public double RotationDegrees { get; init; }
    public double TranslationX { get; init; }
    public double TranslationY { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Scale)
        && Scale > 0d
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(TranslationX)
        && double.IsFinite(TranslationY);

    public MapScreenPoint ToScreen(MapReferencePoint point)
    {
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapScreenPoint(
            ((point.X * cosine) - (point.Y * sine)) * Scale + TranslationX,
            ((point.X * sine) + (point.Y * cosine)) * Scale + TranslationY);
    }

    public MapReferencePoint ToReference(MapScreenPoint point)
    {
        if (!IsValid)
            return new MapReferencePoint(double.NaN, double.NaN);
        var scaledX = (point.X - TranslationX) / Scale;
        var scaledY = (point.Y - TranslationY) / Scale;
        var radians = -RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapReferencePoint(
            (scaledX * cosine) - (scaledY * sine),
            (scaledX * sine) + (scaledY * cosine));
    }

    public MapOverlayTransform ToOverlayTransform(
        int referenceWidth,
        int referenceHeight,
        double residualPixels = 0d) =>
        new()
        {
            ScaleX = Scale,
            ScaleY = Scale,
            OffsetX = TranslationX,
            OffsetY = TranslationY,
            ReferenceCenterX = referenceWidth / 2d,
            ReferenceCenterY = referenceHeight / 2d,
            ScreenCenterX = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).X,
            ScreenCenterY = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).Y,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            OrientationDegrees = NormalizeRotation(RotationDegrees),
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = Math.Max(0d, residualPixels)
        };

    public static MapSimilarityTransform FromOverlay(
        MapOverlayTransform transform) =>
        new()
        {
            Scale = (transform.ScaleX + transform.ScaleY) / 2d,
            RotationDegrees = transform.OrientationDegrees,
            TranslationX = transform.OffsetX,
            TranslationY = transform.OffsetY
        };

    private static int NormalizeRotation(double degrees)
    {
        var normalized = ((int)Math.Round(degrees) % 360 + 360) % 360;
        return normalized;
    }
}
