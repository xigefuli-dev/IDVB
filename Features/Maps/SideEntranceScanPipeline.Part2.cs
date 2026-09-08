using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>
/// 侧门专属扫描管线：对捕获帧运行模板匹配，返回 TopK 候选地图。
/// 与双门管线并列，仅用于首次地图识别，对齐阶段仍由原有管线处理。
/// </summary>
public sealed partial class SideEntranceScanPipeline
{

    /// <summary>
    /// 按给定缩放重采样模板后在 <paramref name="searchRegion"/> 内匹配一次。
    /// 模板放大用 <see cref="InterpolationFlags.Cubic"/>、缩小用
    /// <see cref="InterpolationFlags.Area"/>，避免缩小时的锯齿压低相关性得分。
    /// </summary>
    /// <param name="searchRegion">搜索区域（细化窗口，全分辨率）。</param>
    /// <param name="regionOrigin">
    ///   搜索区域左上角在完整帧中的坐标；匹配位置会加回该原点，
    ///   使返回的 MatchLocation 始终是帧坐标而非窗口内的相对坐标。
    /// </param>
    private static SideEntranceScanCandidate? Evaluate(
        Mat searchRegion,
        Point regionOrigin,
        Mat template,
        MapRecord map,
        string floorKey,
        double scale)
    {
        var width = (int)Math.Round(template.Width * scale);
        var height = (int)Math.Round(template.Height * scale);
        // 模板必须严格小于搜索图：等大时 MatchTemplate 只会产出 1×1 的平凡结果。
        if (width < 8 || height < 8
            || width >= searchRegion.Width || height >= searchRegion.Height)
        {
            return null;
        }

        using var scaled = new Mat();
        Cv2.Resize(
            template,
            scaled,
            new Size(width, height),
            0d,
            0d,
            scale >= 1d ? InterpolationFlags.Cubic : InterpolationFlags.Area);

        using var resultMat = new Mat();
        Cv2.MatchTemplate(
            searchRegion,
            scaled,
            resultMat,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(resultMat, out _, out var maxVal, out _, out var maxLoc);
        if (!double.IsFinite(maxVal))
            return null;

        return new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = floorKey,
            MatchScore = maxVal,
            MatchScale = scale,
            MatchLocation = new MapScreenRect(
                regionOrigin.X + maxLoc.X,
                regionOrigin.Y + maxLoc.Y,
                width,
                height)
        };
    }

    private readonly record struct GateSpatialPrior(
        double GateX,
        double GateY,
        double DeltaRefX,
        double DeltaRefY,
        double MaxResidualPixels);

    private static bool TryCreateGateSpatialPrior(
        MapRecord map,
        string floorKey,
        Mat template,
        GateDetection? detectedGate,
        MapScreenRect? viewportBounds,
        Rect searchBounds,
        out GateSpatialPrior prior)
    {
        prior = default;
        if (detectedGate is null || viewportBounds is not { IsValid: true } vp)
            return false;

        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey);
        if (profile is null || anchor?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0 || profile.RecognitionPixelHeight <= 0)
        {
            return false;
        }

        var anchorCenterX = (anchor.Bounds.X + anchor.Bounds.Width / 2d)
            * profile.RecognitionPixelWidth;
        var anchorCenterY = (anchor.Bounds.Y + anchor.Bounds.Height / 2d)
            * profile.RecognitionPixelHeight;

        var featureCenterX = profile.SideEntranceFeatureCenterX;
        var featureCenterY = profile.SideEntranceFeatureCenterY;
        if (!double.IsFinite(featureCenterX) || featureCenterX <= 0d
            || !double.IsFinite(featureCenterY) || featureCenterY <= 0d)
        {
            featureCenterX = anchorCenterX;
            featureCenterY = anchorCenterY;
        }

        var featureOriginX = featureCenterX - (template.Width / 2d);
        var featureOriginY = featureCenterY - (template.Height / 2d);
        var deltaRefX = anchorCenterX - featureOriginX;
        var deltaRefY = anchorCenterY - featureOriginY;

        var gateX = detectedGate.ScreenBounds.CenterX - vp.X - searchBounds.X;
        var gateY = detectedGate.ScreenBounds.CenterY - vp.Y - searchBounds.Y;

        var maxResidual = Math.Max(32.0d, SideEntranceScanRules.MaximumGateSpatialResidualPixels * 1.5d);
        prior = new GateSpatialPrior(gateX, gateY, deltaRefX, deltaRefY, maxResidual);
        return true;
    }

    /// <summary>
    /// 在（已降采样的）粗帧上遍历缩放网格，返回得分最高的那一档缩放及其
    /// 匹配位置。缩放是相对原始分辨率的，位置则是降采样图坐标；降采样只
    /// 影响搜索成本与位置精度，不影响缩放语义。粗帧由调用方一次性构建并
    /// 共享给全部候选地图，避免每张地图重复降采样完整帧。
    /// 若提供了门空间先验，则在理论门位置周围的小窗口内搜寻极值，防止大离散误差与小尺度伪高分。
    /// </summary>
    private static CoarsePeak? FindCoarsePeak(
        Mat coarseFrame,
        Mat template,
        int coarseFactor,
        GateSpatialPrior? prior,
        string logContext)
    {
        CoarsePeak? bestPeak = null;
        var bestScore = double.NegativeInfinity;
        var response = new List<(double Scale, double Score)>();
        for (var scale = SideEntranceScanRules.MinimumScale;
            scale <= SideEntranceScanRules.MaximumScale;
            scale *= 1d + SideEntranceScanRules.CoarseScaleStep)
        {
            var width = (int)Math.Round(
                template.Width * scale / coarseFactor);
            var height = (int)Math.Round(
                template.Height * scale / coarseFactor);
            if (width < 8 || height < 8
                || width >= coarseFrame.Width || height >= coarseFrame.Height)
            {
                continue;
            }

            using var scaled = new Mat();
            Cv2.Resize(
                template,
                scaled,
                new Size(width, height),
                0d,
                0d,
                InterpolationFlags.Area);
            using var resultMat = new Mat();
            Cv2.MatchTemplate(
                coarseFrame,
                scaled,
                resultMat,
                TemplateMatchModes.CCoeffNormed);

            int maxLocX;
            int maxLocY;
            double maxVal;

            if (prior is { } p)
            {
                var expectedX = p.GateX - (p.DeltaRefX * scale);
                var expectedY = p.GateY - (p.DeltaRefY * scale);
                var expCoarseX = expectedX / coarseFactor;
                var expCoarseY = expectedY / coarseFactor;
                var maxRadius = p.MaxResidualPixels / coarseFactor;

                var minX = Math.Max(0, (int)Math.Floor(expCoarseX - maxRadius));
                var minY = Math.Max(0, (int)Math.Floor(expCoarseY - maxRadius));
                var maxX = Math.Min(resultMat.Width, (int)Math.Ceiling(expCoarseX + maxRadius) + 1);
                var maxY = Math.Min(resultMat.Height, (int)Math.Ceiling(expCoarseY + maxRadius) + 1);

                if (maxX <= minX || maxY <= minY)
                {
                    continue;
                }

                using var subMat = new Mat(resultMat, new Rect(minX, minY, maxX - minX, maxY - minY));
                Cv2.MinMaxLoc(subMat, out _, out maxVal, out _, out var localLoc);
                maxLocX = minX + localLoc.X;
                maxLocY = minY + localLoc.Y;
            }
            else
            {
                Cv2.MinMaxLoc(resultMat, out _, out maxVal, out _, out var loc);
                maxLocX = loc.X;
                maxLocY = loc.Y;
            }

            if (double.IsFinite(maxVal))
                response.Add((scale, maxVal));
            if (double.IsFinite(maxVal) && maxVal > bestScore)
            {
                bestScore = maxVal;
                bestPeak = new CoarsePeak(scale, maxLocX, maxLocY, maxVal);
            }
        }

        if (response.Count > 0)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"侧门粗搜索尺度响应 {logContext}",
                details: new()
                {
                    ["scales"] = string.Join(
                        ",", response.Select(r => r.Scale.ToString("F3"))),
                    ["scores"] = string.Join(
                        ",", response.Select(r => r.Score.ToString("F4")))
                });
        }

        return bestPeak;
    }
}
