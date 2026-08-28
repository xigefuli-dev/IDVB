using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;public sealed partial class MapRecognitionProfile
{

    private static void NormalizeAnnotations(FloorRecognitionProfile floor)
    {
        floor.Annotations ??= [];
        foreach (var annotation in floor.Annotations)
        {
            if (!MapAnnotationColor.TryNormalize(annotation.ColorHex, out var colorHex))
                colorHex = MapAnnotationColor.FromLegacyIndex(annotation.ColorIndex);
            annotation.ColorHex = colorHex;
            annotation.ColorIndex = MapAnnotationColor.ToLegacyIndex(colorHex);
        }
    }

    private static void NormalizeAnchorWeights(FloorRecognitionProfile floor)
    {
        foreach (var anchor in floor.Anchors)
        {
            if (anchor.Weight <= 0 || double.IsNaN(anchor.Weight) || double.IsInfinity(anchor.Weight))
                anchor.Weight = anchor.Role == RecognitionAnchorRole.Required ? 1d : 0.35d;
            else
                anchor.Weight = Math.Clamp(anchor.Weight, 0.05d, 2d);
        }
    }

    private static int NormalizeOrientation(int degrees) => degrees switch
    {
        0 or 90 or 180 or 270 => degrees,
        _ => 0
    };

    private static NormalizedRectangle? NormalizeRecognitionRegion(NormalizedRectangle? region)
    {
        if (region?.IsValid is not true)
            return null;
        var left = Math.Clamp(region.X, 0d, 1d);
        var top = Math.Clamp(region.Y, 0d, 1d);
        var right = Math.Clamp(region.X + region.Width, left, 1d);
        var bottom = Math.Clamp(region.Y + region.Height, top, 1d);
        var normalized = new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid ? normalized : null;
    }

    private static MapReferenceBounds? NormalizeValidMapBounds(
        FloorRecognitionProfile profile)
    {
        if (profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        if (profile.ValidMapBounds?.IsValid is not true)
        {
            return MapReferenceBounds.FullImage(
                profile.RecognitionPixelWidth,
                profile.RecognitionPixelHeight);
        }

        var bounds = profile.ValidMapBounds;
        var left = Math.Clamp(bounds.X, 0d, profile.RecognitionPixelWidth);
        var top = Math.Clamp(bounds.Y, 0d, profile.RecognitionPixelHeight);
        var right = Math.Clamp(
            bounds.Right,
            left,
            profile.RecognitionPixelWidth);
        var bottom = Math.Clamp(
            bounds.Bottom,
            top,
            profile.RecognitionPixelHeight);
        var normalized = new MapReferenceBounds
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid
            ? normalized
            : MapReferenceBounds.FullImage(
                profile.RecognitionPixelWidth,
                profile.RecognitionPixelHeight);
    }
}
