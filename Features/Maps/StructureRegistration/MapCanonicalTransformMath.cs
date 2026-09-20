namespace IDVBuff.Features.Maps;

/// <summary>
/// Unified shared mathematical operations for coordinate transformations between
/// screen, viewport, query ROI, and reference spaces in VPSG2 and VPSG3.
/// </summary>
public static class MapCanonicalTransformMath
{
    /// <summary>
    /// Computes canonical screen offset from matching space parameters:
    /// Offset = ViewportOrigin + QueryBoundsOffset * scale - LogicalReferenceOffset * scale.
    /// </summary>
    public static (double OffsetX, double OffsetY) ComputeScreenOffset(
        double viewportX,
        double viewportY,
        double queryBoundsX,
        double queryBoundsY,
        double logicalReferenceX,
        double logicalReferenceY,
        double matchingScale)
    {
        var offsetX = viewportX + (queryBoundsX * matchingScale) - (logicalReferenceX * matchingScale);
        var offsetY = viewportY + (queryBoundsY * matchingScale) - (logicalReferenceY * matchingScale);
        return (offsetX, offsetY);
    }

    /// <summary>
    /// Computes physical scale when the reference image was downsampled by referenceScale:
    /// actualScale = matchingScale * referenceScale.
    /// </summary>
    public static double ComputeActualScale(double matchingScale, double referenceScale)
    {
        return matchingScale * referenceScale;
    }

    /// <summary>
    /// Converts unscaled reference coordinates to screen coordinates:
    /// Screen = Reference * scale + Offset.
    /// </summary>
    public static (double ScreenX, double ScreenY) ReferenceToScreen(
        double referenceX,
        double referenceY,
        double scale,
        double offsetX,
        double offsetY)
    {
        return (
            (referenceX * scale) + offsetX,
            (referenceY * scale) + offsetY);
    }

    /// <summary>
    /// Converts screen coordinates back to unscaled reference coordinates:
    /// Reference = (Screen - Offset) / scale.
    /// </summary>
    public static (double ReferenceX, double ReferenceY) ScreenToReference(
        double screenX,
        double screenY,
        double scale,
        double offsetX,
        double offsetY)
    {
        if (Math.Abs(scale) < 1e-9)
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale cannot be zero.");

        return (
            (screenX - offsetX) / scale,
            (screenY - offsetY) / scale);
    }

    /// <summary>
    /// Computes reference space coordinates of the viewport origin:
    /// ViewportOrigin = (ViewportBounds.TopLeft - Offset) / actualScale.
    /// </summary>
    public static MapViewportOrigin ComputeViewportOrigin(
        double viewportX,
        double viewportY,
        double offsetX,
        double offsetY,
        double actualScale)
    {
        var (refX, refY) = ScreenToReference(viewportX, viewportY, actualScale, offsetX, offsetY);
        return new MapViewportOrigin(refX, refY);
    }

    /// <summary>
    /// Scales a computation-space transform up to physical screen pixels:
    /// Scale, Offset, ScreenCenter, and Residual are multiplied by ratio.
    /// ReferenceCenter and Reference dimensions remain in unscaled reference pixels.
    /// </summary>
    public static MapOverlayTransform ToPhysicalTransform(
        MapOverlayTransform value,
        double ratio)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(ratio) || ratio <= 0d)
            throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be a positive finite number.");

        return new MapOverlayTransform
        {
            ScaleX = value.ScaleX * ratio,
            ScaleY = value.ScaleY * ratio,
            OffsetX = value.OffsetX * ratio,
            OffsetY = value.OffsetY * ratio,
            ReferenceCenterX = value.ReferenceCenterX,
            ReferenceCenterY = value.ReferenceCenterY,
            ScreenCenterX = value.ScreenCenterX * ratio,
            ScreenCenterY = value.ScreenCenterY * ratio,
            ReferenceWidth = value.ReferenceWidth,
            ReferenceHeight = value.ReferenceHeight,
            OrientationDegrees = value.OrientationDegrees,
            AlignmentMode = value.AlignmentMode,
            MaximumResidualPixels = value.MaximumResidualPixels * ratio,
            UsedDegenerateAxisFallback = value.UsedDegenerateAxisFallback
        };
    }

    /// <summary>
    /// Scales a physical-space transform down to computation space:
    /// Scale, Offset, ScreenCenter, and Residual are divided by ratio.
    /// </summary>
    public static MapOverlayTransform ToComputationTransform(
        MapOverlayTransform value,
        double ratio)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(ratio) || ratio <= 0d)
            throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be a positive finite number.");

        return ToPhysicalTransform(value, 1d / ratio);
    }

    /// <summary>
    /// Constructs a standard MapOverlayTransform from registration parameters.
    /// </summary>
    public static MapOverlayTransform BuildOverlayTransform(
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        int referenceWidth,
        int referenceHeight,
        double residualPixels = 0d,
        int orientationDegrees = 0,
        MapOverlayAlignmentMode alignmentMode = MapOverlayAlignmentMode.Uniform,
        bool usedDegenerateAxisFallback = false)
    {
        var refCenterX = referenceWidth / 2d;
        var refCenterY = referenceHeight / 2d;
        return new MapOverlayTransform
        {
            ScaleX = scaleX,
            ScaleY = scaleY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = refCenterX,
            ReferenceCenterY = refCenterY,
            ScreenCenterX = (refCenterX * scaleX) + offsetX,
            ScreenCenterY = (refCenterY * scaleY) + offsetY,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            OrientationDegrees = orientationDegrees,
            AlignmentMode = alignmentMode,
            MaximumResidualPixels = residualPixels,
            UsedDegenerateAxisFallback = usedDegenerateAxisFallback
        };
    }
}
