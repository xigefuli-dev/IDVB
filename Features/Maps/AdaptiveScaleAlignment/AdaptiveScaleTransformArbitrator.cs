namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal static class AdaptiveScaleTransformArbitrator
{
    public static MapOverlayTransform KeepScale(
        MapOverlayTransform candidate,
        double scale) => WithScale(candidate, scale);

    private static MapOverlayTransform WithScale(MapOverlayTransform source, double scale)
    {
        var referenceCenterX = source.ReferenceCenterX;
        var referenceCenterY = source.ReferenceCenterY;
        var screenCenterX = source.ScreenCenterX;
        var screenCenterY = source.ScreenCenterY;
        if (!double.IsFinite(screenCenterX))
            screenCenterX = (referenceCenterX * source.ScaleX) + source.OffsetX;
        if (!double.IsFinite(screenCenterY))
            screenCenterY = (referenceCenterY * source.ScaleY) + source.OffsetY;

        return new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = screenCenterX - (referenceCenterX * scale),
            OffsetY = screenCenterY - (referenceCenterY * scale),
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = screenCenterX,
            ScreenCenterY = screenCenterY,
            ReferenceWidth = source.ReferenceWidth,
            ReferenceHeight = source.ReferenceHeight,
            OrientationDegrees = source.OrientationDegrees,
            AlignmentMode = source.AlignmentMode,
            MaximumResidualPixels = source.MaximumResidualPixels,
            UsedDegenerateAxisFallback = source.UsedDegenerateAxisFallback
        };
    }
}
