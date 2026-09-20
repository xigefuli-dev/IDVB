namespace IDVBuff.Features.Maps;

/// <summary>
/// Creates a neutral scale seed for one exact floor. Scale evidence from any
/// other floor is deliberately excluded; same-floor sessions and caches may
/// replace this seed later in the alignment pipeline.
/// </summary>
internal static class MapFloorScaleSeedRules
{
    public static MapOverlayTransform CreateIndependentFloorSeed(
        MapRecord map,
        string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? throw new InvalidOperationException(
                $"地图不包含楼层 '{floorKey}'。");
        var width = Math.Max(1, profile.RecognitionPixelWidth);
        var height = Math.Max(1, profile.RecognitionPixelHeight);
        return new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceCenterX = width / 2d,
            ReferenceCenterY = height / 2d,
            ScreenCenterX = width / 2d,
            ScreenCenterY = height / 2d,
            ReferenceWidth = width,
            ReferenceHeight = height,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }

    public static bool IsNeutralIndependentSeed(MapOverlayTransform? transform) =>
        transform is not null
        && Math.Abs(transform.ScaleX - 1d) < 1e-6
        && Math.Abs(transform.ScaleY - 1d) < 1e-6
        && Math.Abs(transform.OffsetX) < 1e-6
        && Math.Abs(transform.OffsetY) < 1e-6;
}
/*
 * 文件职责：MapFloorScaleSeedRules。所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 维护约束：涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定。
 */
