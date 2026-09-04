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
        var isHalfOrigin = Math.Abs(bounds.X) <= 1d && Math.Abs(bounds.Y) <= 1d;
        var widthHalfMatch = Math.Abs((bounds.Width * 2d) - profile.RecognitionPixelWidth) <= Math.Max(3d, profile.RecognitionPixelWidth * 0.02d);
        var heightHalfMatch = Math.Abs((bounds.Height * 2d) - profile.RecognitionPixelHeight) <= Math.Max(3d, profile.RecognitionPixelHeight * 0.02d);
        if (isHalfOrigin && widthHalfMatch && heightHalfMatch)
        {
            return MapReferenceBounds.FullImage(
                profile.RecognitionPixelWidth,
                profile.RecognitionPixelHeight);
        }
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

    internal static MapReferenceBounds ResolveEffectiveValidMapBounds(
        FloorRecognitionProfile profile,
        int? referenceWidth = null,
        int? referenceHeight = null)
    {
        var targetWidth = referenceWidth is > 0
            ? referenceWidth.Value
            : profile.RecognitionPixelWidth;
        var targetHeight = referenceHeight is > 0
            ? referenceHeight.Value
            : profile.RecognitionPixelHeight;

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return profile.ValidMapBounds?.IsValid is true
                ? profile.ValidMapBounds.Clone()
                : new MapReferenceBounds();
        }

        if (profile.ValidMapBounds?.IsValid is not true)
        {
            return MapReferenceBounds.FullImage(targetWidth, targetHeight);
        }

        var bounds = profile.ValidMapBounds.Clone();
        var isHalfOrigin = Math.Abs(bounds.X) <= 1d && Math.Abs(bounds.Y) <= 1d;
        var widthHalfMatch = Math.Abs((bounds.Width * 2d) - targetWidth) <= Math.Max(3d, targetWidth * 0.02d);
        var heightHalfMatch = Math.Abs((bounds.Height * 2d) - targetHeight) <= Math.Max(3d, targetHeight * 0.02d);

        if (isHalfOrigin && widthHalfMatch && heightHalfMatch)
        {
            return MapReferenceBounds.FullImage(targetWidth, targetHeight);
        }

        if (profile.RecognitionPixelWidth > 0 && profile.RecognitionPixelHeight > 0
            && (profile.RecognitionPixelWidth != targetWidth || profile.RecognitionPixelHeight != targetHeight))
        {
            var scaleX = (double)targetWidth / profile.RecognitionPixelWidth;
            var scaleY = (double)targetHeight / profile.RecognitionPixelHeight;
            var scaledLeft = Math.Clamp(bounds.X * scaleX, 0d, targetWidth);
            var scaledTop = Math.Clamp(bounds.Y * scaleY, 0d, targetHeight);
            var scaledRight = Math.Clamp(bounds.Right * scaleX, scaledLeft, targetWidth);
            var scaledBottom = Math.Clamp(bounds.Bottom * scaleY, scaledTop, targetHeight);
            var scaled = new MapReferenceBounds
            {
                X = scaledLeft,
                Y = scaledTop,
                Width = scaledRight - scaledLeft,
                Height = scaledBottom - scaledTop
            };
            return scaled.IsValid
                ? scaled
                : MapReferenceBounds.FullImage(targetWidth, targetHeight);
        }

        if (bounds.Right * 2d <= targetWidth + 4d && bounds.Bottom * 2d <= targetHeight + 4d
            && widthHalfMatch && heightHalfMatch)
        {
            var scaledLeft = Math.Clamp(bounds.X * 2d, 0d, targetWidth);
            var scaledTop = Math.Clamp(bounds.Y * 2d, 0d, targetHeight);
            var scaledRight = Math.Clamp(bounds.Right * 2d, scaledLeft, targetWidth);
            var scaledBottom = Math.Clamp(bounds.Bottom * 2d, scaledTop, targetHeight);
            var scaled = new MapReferenceBounds
            {
                X = scaledLeft,
                Y = scaledTop,
                Width = scaledRight - scaledLeft,
                Height = scaledBottom - scaledTop
            };
            return scaled.IsValid
                ? scaled
                : MapReferenceBounds.FullImage(targetWidth, targetHeight);
        }

        var left = Math.Clamp(bounds.X, 0d, targetWidth);
        var top = Math.Clamp(bounds.Y, 0d, targetHeight);
        var right = Math.Clamp(bounds.Right, left, targetWidth);
        var bottom = Math.Clamp(bounds.Bottom, top, targetHeight);
        var normalized = new MapReferenceBounds
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid
            ? normalized
            : MapReferenceBounds.FullImage(targetWidth, targetHeight);
    }
}
