namespace IDVBuff.Features.Maps;

/// <summary>Solves an axis-aligned scale and translation from the two identified gate centers.</summary>
public static class MapOverlayTransformSolver
{
    public static double ExactFitTolerancePixels =>
        RecognitionConfigRules.ExactFitTolerancePixels;
    private static double MinimumScale =>
        RecognitionConfigRules.MinimumScale;
    private static double MaximumScale =>
        RecognitionConfigRules.MaximumScale;
    private static double MinimumStableAxisPixels =>
        RecognitionConfigRules.MinimumStableAxisPixels;
    private static double StableAxisDistanceRatio =>
        RecognitionConfigRules.StableAxisDistanceRatio;

    public static bool TrySolve(
        MapGeometryCandidate candidate,
        MapOverlayAlignmentMode mode,
        out MapOverlayTransform transform,
        out string failureReason)
    {
        transform = new MapOverlayTransform();
        failureReason = string.Empty;
        if (!Enum.IsDefined(mode))
        {
            failureReason = "未知的图层对齐模式。";
            return false;
        }

        var fingerprint = candidate.Fingerprint;
        if (fingerprint.ReferenceWidth <= 0 || fingerprint.ReferenceHeight <= 0)
        {
            failureReason = "参考地图裁切尺寸无效。";
            return false;
        }

        var referenceMain = new MapNormalizedPoint(
            fingerprint.MainPoint.X * fingerprint.ReferenceWidth,
            fingerprint.MainPoint.Y * fingerprint.ReferenceHeight);
        var referenceSide = new MapNormalizedPoint(
            fingerprint.SidePoint.X * fingerprint.ReferenceWidth,
            fingerprint.SidePoint.Y * fingerprint.ReferenceHeight);
        var screenMain = new MapNormalizedPoint(
            candidate.MainGate.ScreenBounds.CenterX,
            candidate.MainGate.ScreenBounds.CenterY);
        var screenSide = new MapNormalizedPoint(
            candidate.SideGate.ScreenBounds.CenterX,
            candidate.SideGate.ScreenBounds.CenterY);
        var referenceDeltaX = referenceSide.X - referenceMain.X;
        var referenceDeltaY = referenceSide.Y - referenceMain.Y;
        var screenDeltaX = screenSide.X - screenMain.X;
        var screenDeltaY = screenSide.Y - screenMain.Y;
        var referenceDistance = Length(referenceDeltaX, referenceDeltaY);
        var screenDistance = Length(screenDeltaX, screenDeltaY);
        if (referenceDistance <= 1d || screenDistance <= 1d)
        {
            failureReason = "两个门点距离过小，无法计算图层缩放。";
            return false;
        }

        double scaleX;
        double scaleY;
        var usedFallback = false;
        if (mode == MapOverlayAlignmentMode.Uniform)
        {
            var denominator = (referenceDeltaX * referenceDeltaX) + (referenceDeltaY * referenceDeltaY);
            var uniformScale = (
                (referenceDeltaX * screenDeltaX)
                + (referenceDeltaY * screenDeltaY)) / denominator;
            if (!IsValidScale(uniformScale))
            {
                failureReason = "双门方向或等比缩放倍率无效。";
                return false;
            }
            scaleX = uniformScale;
            scaleY = uniformScale;
        }
        else
        {
            var stableThreshold = Math.Max(
                MinimumStableAxisPixels,
                referenceDistance * StableAxisDistanceRatio);
            var xIsStable = Math.Abs(referenceDeltaX) >= stableThreshold;
            var yIsStable = Math.Abs(referenceDeltaY) >= stableThreshold;
            if (!xIsStable && !yIsStable)
            {
                failureReason = "双门向量没有可用于缩放的稳定轴。";
                return false;
            }

            double? solvedX = xIsStable ? screenDeltaX / referenceDeltaX : null;
            double? solvedY = yIsStable ? screenDeltaY / referenceDeltaY : null;
            if (solvedX is { } x && !IsValidScale(x))
            {
                failureReason = "横向缩放会产生镜像或异常倍率。";
                return false;
            }
            if (solvedY is { } y && !IsValidScale(y))
            {
                failureReason = "纵向缩放会产生镜像或异常倍率。";
                return false;
            }

            usedFallback = solvedX is null || solvedY is null;
            scaleX = solvedX ?? solvedY!.Value;
            scaleY = solvedY ?? solvedX!.Value;
        }

        // The live gate midpoint is the map's runtime center. It moves with
        // map panning and is therefore a safer origin than the calibrated ROI.
        var referenceCenter = Midpoint(referenceMain, referenceSide);
        var screenCenter = Midpoint(screenMain, screenSide);
        var offsetX = screenCenter.X - (referenceCenter.X * scaleX);
        var offsetY = screenCenter.Y - (referenceCenter.Y * scaleY);
        var mainResidual = Length(
            ((referenceMain.X * scaleX) + offsetX) - screenMain.X,
            ((referenceMain.Y * scaleY) + offsetY) - screenMain.Y);
        var sideResidual = Length(
            ((referenceSide.X * scaleX) + offsetX) - screenSide.X,
            ((referenceSide.Y * scaleY) + offsetY) - screenSide.Y);
        var maximumResidual = Math.Max(mainResidual, sideResidual);
        if (!double.IsFinite(offsetX)
            || !double.IsFinite(offsetY)
            || !double.IsFinite(maximumResidual))
        {
            failureReason = "图层缩放或位移计算产生了无效结果。";
            return false;
        }

        transform = new MapOverlayTransform
        {
            ScaleX = scaleX,
            ScaleY = scaleY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = referenceCenter.X,
            ReferenceCenterY = referenceCenter.Y,
            ScreenCenterX = screenCenter.X,
            ScreenCenterY = screenCenter.Y,
            ReferenceWidth = fingerprint.ReferenceWidth,
            ReferenceHeight = fingerprint.ReferenceHeight,
            OrientationDegrees = 0,
            AlignmentMode = mode,
            MaximumResidualPixels = maximumResidual,
            UsedDegenerateAxisFallback = usedFallback
        };
        return true;
    }

    public static bool TryTranslateWithLockedScale(
        MapOverlayTransform locked,
        IReadOnlyList<CvAnchorEvidence> matches,
        out MapOverlayTransform transform,
        out string failureReason)
    {
        transform = new MapOverlayTransform();
        failureReason = string.Empty;
        if (matches.Count == 0)
        {
            failureReason = "没有可用于更新平移的锚点。";
            return false;
        }
        if (!IsValidScale(locked.ScaleX)
            || !IsValidScale(locked.ScaleY)
            || locked.ReferenceWidth <= 0
            || locked.ReferenceHeight <= 0)
        {
            failureReason = "锁定的地图缩放无效，需要双门重新对齐。";
            return false;
        }

        var weighted = matches
            .Select(match => new
            {
                Match = match,
                Weight = Math.Max(0.0001d, match.Score),
                OffsetX = match.ScreenBounds.CenterX
                    - (match.ReferenceBounds.CenterX * locked.ScaleX),
                OffsetY = match.ScreenBounds.CenterY
                    - (match.ReferenceBounds.CenterY * locked.ScaleY)
            })
            .ToArray();
        var totalWeight = weighted.Sum(item => item.Weight);
        var offsetX = weighted.Sum(item => item.OffsetX * item.Weight) / totalWeight;
        var offsetY = weighted.Sum(item => item.OffsetY * item.Weight) / totalWeight;
        var maximumResidual = weighted.Max(item => Length(
            ((item.Match.ReferenceBounds.CenterX * locked.ScaleX) + offsetX)
                - item.Match.ScreenBounds.CenterX,
            ((item.Match.ReferenceBounds.CenterY * locked.ScaleY) + offsetY)
                - item.Match.ScreenBounds.CenterY));
        if (!double.IsFinite(offsetX)
            || !double.IsFinite(offsetY)
            || !double.IsFinite(maximumResidual))
        {
            failureReason = "锚点平移计算产生了无效结果。";
            return false;
        }

        var referenceCenterX = weighted.Sum(
            item => item.Match.ReferenceBounds.CenterX * item.Weight) / totalWeight;
        var referenceCenterY = weighted.Sum(
            item => item.Match.ReferenceBounds.CenterY * item.Weight) / totalWeight;
        var screenCenterX = weighted.Sum(
            item => item.Match.ScreenBounds.CenterX * item.Weight) / totalWeight;
        var screenCenterY = weighted.Sum(
            item => item.Match.ScreenBounds.CenterY * item.Weight) / totalWeight;
        transform = new MapOverlayTransform
        {
            ScaleX = locked.ScaleX,
            ScaleY = locked.ScaleY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = screenCenterX,
            ScreenCenterY = screenCenterY,
            ReferenceWidth = locked.ReferenceWidth,
            ReferenceHeight = locked.ReferenceHeight,
            OrientationDegrees = locked.OrientationDegrees,
            AlignmentMode = locked.AlignmentMode,
            MaximumResidualPixels = maximumResidual,
            UsedDegenerateAxisFallback = locked.UsedDegenerateAxisFallback
        };
        return true;
    }

    private static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale;

    private static MapNormalizedPoint Midpoint(
        MapNormalizedPoint left,
        MapNormalizedPoint right) =>
        new((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));
}
