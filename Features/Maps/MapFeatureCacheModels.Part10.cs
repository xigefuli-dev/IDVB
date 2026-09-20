using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static MapOverlayTransform CreateScaleSeed(
        MapRecord map,
        string floorKey,
        double uniformScale,
        double offsetX = 0d,
        double offsetY = 0d)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        var width = Math.Max(1, profile.RecognitionPixelWidth);
        var height = Math.Max(1, profile.RecognitionPixelHeight);
        return new MapOverlayTransform
        {
            ScaleX = uniformScale,
            ScaleY = uniformScale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = width / 2d,
            ReferenceCenterY = height / 2d,
            ScreenCenterX = (width * uniformScale / 2d) + offsetX,
            ScreenCenterY = (height * uniformScale / 2d) + offsetY,
            ReferenceWidth = width,
            ReferenceHeight = height,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }
}
