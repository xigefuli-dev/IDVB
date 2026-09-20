using OpenCvSharp;
using IDVBuff.Pipeline;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门专属扫描管线：基于二值结构线几何先验与稀疏边缘点检验的快速评价实现。
/// 彻底废除旧版滑窗灰度模板匹配，以门锚点推演平移残差与尺度网格，实现 &lt;1ms 级极速评价。
/// </summary>
public sealed partial class SideEntranceScanPipeline
{
    internal static SideEntranceScanCandidate? EvaluateStructuralCandidate(
        MapRecord map,
        string floorKey,
        Mat template,
        IReadOnlyList<Point> sparsePoints,
        Mat validMask,
        double gx,
        double gy,
        MapScreenRect? viewportBounds = null)
    {
        if (template.Empty() || sparsePoints.Count == 0)
            return null;

        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey);
        if (profile is null || anchor?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0 || profile.RecognitionPixelHeight <= 0)
        {
            return null;
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

        var deltaAnchorRefX = anchorCenterX - featureCenterX;
        var deltaAnchorRefY = anchorCenterY - featureCenterY;

        var tplWidth = template.Width;
        var tplHeight = template.Height;

        var bestScore = double.NegativeInfinity;
        var bestScale = 1.0d;
        var bestDeltaX = 0d;
        var bestDeltaY = 0d;

        var minScale = SideEntranceScanRules.MinimumScale;
        var maxScale = SideEntranceScanRules.MaximumScale;
        var coarseStep = SideEntranceScanRules.CoarseScaleStep;

        var vpWidth = viewportBounds?.Width > 0 ? viewportBounds.Value.Width : 1300d;
        var nominalScale = Math.Clamp(vpWidth / profile.RecognitionPixelWidth, minScale, maxScale);

        // 仅保留实机门附近的边缘点，在实机屏幕尺度下约束范围，避免不同底图分辨率导致分母爆炸
        var screenHalfW = (tplWidth / 2d) * nominalScale;
        var screenHalfH = (tplHeight / 2d) * nominalScale;
        var maxExtentX = Math.Max(32d, Math.Min(350d, screenHalfW * 1.25d));
        var maxExtentY = Math.Max(32d, Math.Min(350d, screenHalfH * 1.25d));
        var localPoints = new List<(double rx, double ry)>(sparsePoints.Count);
        for (var i = 0; i < sparsePoints.Count; i++)
        {
            var rx = sparsePoints[i].X - gx;
            var ry = sparsePoints[i].Y - gy;
            if (Math.Abs(rx) <= maxExtentX && Math.Abs(ry) <= maxExtentY)
            {
                localPoints.Add((rx, ry));
            }
        }

        var localCount = localPoints.Count;
        if (localCount == 0)
            return null;

        var nPoints = localCount;
        var relX = new double[nPoints];
        var relY = new double[nPoints];
        for (var i = 0; i < nPoints; i++)
        {
            relX[i] = localPoints[i].rx;
            relY[i] = localPoints[i].ry;
        }

        // 多级膨胀构建屏幕空间一致的距离衰减核（Cone Filter）：
        // 梯度层在屏幕约 2.5px，基础捕获层在屏幕约 5.5px；依据 nominalScale 换算为模板空间半径，
        // 保证滋酥居、展十、爱吃醋、困难等不同底图分辨率在实机屏幕上具有完全一致的几何容差。
        var rPeak = Math.Max(0, (int)Math.Round(0.8d / nominalScale) - 1);
        var rGrad = Math.Max(rPeak + 1, (int)Math.Round(2.5d / nominalScale));
        var rCap = Math.Max(rGrad + 1, (int)Math.Round(5.5d / nominalScale));

        using var kGrad = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * rGrad + 1, 2 * rGrad + 1));
        using var dilatedGrad = new Mat();
        Cv2.Dilate(template, dilatedGrad, kGrad);

        using var kCap = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * rCap + 1, 2 * rCap + 1));
        using var dilatedCap = new Mat();
        Cv2.Dilate(template, dilatedCap, kCap);

        using var dilatedPeak = rPeak > 0 ? new Mat() : null;
        if (rPeak > 0)
        {
            using var kPeak = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * rPeak + 1, 2 * rPeak + 1));
            Cv2.Dilate(template, dilatedPeak!, kPeak);
        }
        var peakSource = dilatedPeak ?? template;

        var step = (int)template.Step();
        var rawBytes = new byte[tplHeight * step];
        var d3Bytes = new byte[tplHeight * step];
        var d5Bytes = new byte[tplHeight * step];
        Marshal.Copy(peakSource.Data, rawBytes, 0, rawBytes.Length);
        Marshal.Copy(dilatedGrad.Data, d3Bytes, 0, d3Bytes.Length);
        Marshal.Copy(dilatedCap.Data, d5Bytes, 0, d5Bytes.Length);

        // 以 1.0d 为中心对齐网格，同时纳入 nominalScale 及其邻域，消除鞍点死区
        var scaleGrid = new List<double>(35);
        for (var s = 1.0d; s <= maxScale; s *= 1d + coarseStep)
            scaleGrid.Add(s);
        for (var s = 1.0d / (1d + coarseStep); s >= minScale; s /= 1d + coarseStep)
            scaleGrid.Add(s);
        if (nominalScale >= minScale && nominalScale <= maxScale)
        {
            scaleGrid.Add(nominalScale);
            scaleGrid.Add(nominalScale * (1d + coarseStep * 0.5d));
            scaleGrid.Add(nominalScale / (1d + coarseStep * 0.5d));
        }
        scaleGrid.Sort();

        // 阶段一：粗尺度与整数残差搜索（覆盖门检测中心 [-8, 8] 像素内的搜索范围，步长 2）
        for (var scaleIdx = 0; scaleIdx < scaleGrid.Count; scaleIdx++)
        {
            var scale = scaleGrid[scaleIdx];
            var invS = 1.0d / scale;
            var baseTx = (tplWidth / 2d) + deltaAnchorRefX;
            var baseTy = (tplHeight / 2d) + deltaAnchorRefY;

            for (var dx = -8; dx <= 8; dx += 2)
            {
                var shiftTx = baseTx - (dx * invS);
                for (var dy = -8; dy <= 8; dy += 2)
                {
                    var shiftTy = baseTy - (dy * invS);

                    var hit5 = 0;
                    var hit3 = 0;
                    var hit1 = 0;
                    var testedPoints = 0;

                    for (var i = 0; i < nPoints; i++)
                    {
                        var tx = shiftTx + (relX[i] * invS);
                        var ty = shiftTy + (relY[i] * invS);

                        var ix = (int)Math.Round(tx);
                        var iy = (int)Math.Round(ty);

                        if (ix >= 0 && ix < tplWidth && iy >= 0 && iy < tplHeight)
                        {
                            testedPoints++;
                            var offset = iy * step + ix;
                            if (d5Bytes[offset] > 128)
                            {
                                hit5++;
                                if (d3Bytes[offset] > 128)
                                {
                                    hit3++;
                                    if (rawBytes[offset] > 128)
                                        hit1++;
                                }
                            }
                        }
                    }

                    var supportFactor = Math.Min(1.0d, testedPoints / 18.0d);
                    var weightedHits = (hit5 * 0.4d) + (hit3 * 0.3d) + (hit1 * 0.3d);
                    var hitRatio = testedPoints > 0 ? weightedHits / testedPoints : 0d;
                    var score = hitRatio * supportFactor;

                    var isBetter = score > bestScore + 1e-4d ||
                        (Math.Abs(score - bestScore) <= 1e-4d &&
                         Math.Abs(scale - 1.0d) < Math.Abs(bestScale - 1.0d));

                    if (isBetter)
                    {
                        bestScore = score;
                        bestScale = scale;
                        bestDeltaX = dx;
                        bestDeltaY = dy;
                    }
                }
            }
        }

        // 阶段二：峰值附近的细化搜索
        if (bestScore > 0.3d)
        {
            var refineSteps = SideEntranceScanRules.RefineStepsPerSide;
            var fineStep = coarseStep / (refineSteps + 1d);
            var fineJitters = new (double dx, double dy)[]
            {
                (bestDeltaX, bestDeltaY),
                (bestDeltaX - 1, bestDeltaY), (bestDeltaX + 1, bestDeltaY),
                (bestDeltaX, bestDeltaY - 1), (bestDeltaX, bestDeltaY + 1),
                (bestDeltaX - 1, bestDeltaY - 1), (bestDeltaX + 1, bestDeltaY - 1),
                (bestDeltaX - 1, bestDeltaY + 1), (bestDeltaX + 1, bestDeltaY + 1)
            };

            for (var stepIdx = -refineSteps; stepIdx <= refineSteps; stepIdx++)
            {
                if (stepIdx == 0) continue;
                var scale = bestScale * (1d + stepIdx * fineStep);
                if (scale < minScale || scale > maxScale)
                    continue;

                var invS = 1.0d / scale;
                var baseTx = (tplWidth / 2d) + deltaAnchorRefX;
                var baseTy = (tplHeight / 2d) + deltaAnchorRefY;

                for (var j = 0; j < fineJitters.Length; j++)
                {
                    var (dx, dy) = fineJitters[j];
                    var shiftTx = baseTx - (dx * invS);
                    var shiftTy = baseTy - (dy * invS);

                    var hit5 = 0;
                    var hit3 = 0;
                    var hit1 = 0;
                    var testedPoints = 0;

                    for (var i = 0; i < nPoints; i++)
                    {
                        var tx = shiftTx + (relX[i] * invS);
                        var ty = shiftTy + (relY[i] * invS);

                        var ix = (int)Math.Round(tx);
                        var iy = (int)Math.Round(ty);

                        if (ix >= 0 && ix < tplWidth && iy >= 0 && iy < tplHeight)
                        {
                            testedPoints++;
                            var offset = iy * step + ix;
                            if (d5Bytes[offset] > 128)
                            {
                                hit5++;
                                if (d3Bytes[offset] > 128)
                                {
                                    hit3++;
                                    if (rawBytes[offset] > 128)
                                        hit1++;
                                }
                            }
                        }
                    }

                    var supportFactor = Math.Min(1.0d, testedPoints / 18.0d);
                    var weightedHits = (hit5 * 0.4d) + (hit3 * 0.3d) + (hit1 * 0.3d);
                    var hitRatio = testedPoints > 0 ? weightedHits / testedPoints : 0d;
                    var score = hitRatio * supportFactor;

                    var isBetter = score > bestScore + 1e-4d ||
                        (Math.Abs(score - bestScore) <= 1e-4d &&
                         Math.Abs(scale - 1.0d) < Math.Abs(bestScale - 1.0d));

                    if (isBetter)
                    {
                        bestScore = score;
                        bestScale = scale;
                        bestDeltaX = dx;
                        bestDeltaY = dy;
                    }
                }
            }
        }

        if (bestScore <= 0d || !double.IsFinite(bestScore))
            return null;

        var featureOriginX = featureCenterX - (tplWidth / 2d);
        var featureOriginY = featureCenterY - (tplHeight / 2d);
        var matchLocX = (gx + bestDeltaX) - ((anchorCenterX - featureOriginX) * bestScale);
        var matchLocY = (gy + bestDeltaY) - ((anchorCenterY - featureOriginY) * bestScale);
        var matchWidth = (int)Math.Round(tplWidth * bestScale);
        var matchHeight = (int)Math.Round(tplHeight * bestScale);

        return new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = floorKey,
            MatchScore = bestScore,
            MatchScale = bestScale,
            MatchLocation = new MapScreenRect(matchLocX, matchLocY, matchWidth, matchHeight),
            GateSpatialResidualPixels = Math.Sqrt(bestDeltaX * bestDeltaX + bestDeltaY * bestDeltaY),
            Disposition = SideEntranceCandidateDisposition.NeedsVerification
        };
    }
}
