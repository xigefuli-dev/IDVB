using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record NativeMiniMapHeading(
    PlayerSlot Slot, double Degrees, double Confidence, double ConeContrast, Point2d Center);

/// <summary>Reads the numbered marker and its view cone, independently of map registration.</summary>
public sealed class NativeMiniMapHeadingDetector : IDisposable
{
    private sealed record Template(PlayerSlot Slot, Mat Gray, double CenterX, double CenterY, double Radius);
    private readonly List<Template> _templates = [];
    private PlayerSlot? _lockedSlot;
    private int _missingLockedSlotCount;

    public NativeMiniMapHeadingDetector(Func<PlayerSlot, string>? resolvePath = null)
    {
        try
        {
            foreach (var slot in MapPlayerAssetCatalog.Slots)
            {
                using var source = Cv2.ImDecode(File.ReadAllBytes(
                    (resolvePath ?? MapPlayerAssetCatalog.ResolvePath)(slot)), ImreadModes.Grayscale);
                if (source.Empty()) throw new IOException($"玩家序号模板为空：{slot}");
                foreach (var scale in PlayerTrackingRules.TemplateScaleCandidates)
                {
                    using var resized = new Mat();
                    Cv2.Resize(source, resized, new Size(), scale, scale, InterpolationFlags.Linear);
                    var x = (int)(resized.Width * .33);
                    var y = (int)(resized.Height * .22);
                    using var digit = new Mat(resized, new Rect(x, y,
                        (int)(resized.Width * .67) - x, (int)(resized.Height * .78) - y));
                    _templates.Add(new(slot, digit.Clone(), resized.Width * .5 - x,
                        resized.Height * .46 - y, Math.Min(resized.Width, resized.Height) * .43));
                }
            }
        }
        catch { Dispose(); throw; }
    }

    public NativeMiniMapHeading? Detect(Mat image, out string reason)
    {
        reason = "marker_not_found";
        if (image.Empty() || image.Channels() is not (3 or 4)) return null;
        using var bgr = new Mat();
        if (image.Channels() == 4) Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR);
        else image.CopyTo(bgr);
        using var gray = new Mat();
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var imgCenter = new Point2d(gray.Width * 0.5, gray.Height * 0.5);
        var minDim = Math.Min(gray.Width, gray.Height);

        // 如果锁定槽位连续 25 帧在中心区域失踪，自动解除槽位锁以自愈
        if (_lockedSlot is not null && _missingLockedSlotCount > 25)
        {
            _lockedSlot = null;
            _missingLockedSlotCount = 0;
        }

        var candidates = new Dictionary<PlayerSlot, (Template Template, Point Point, double Score, double DistNorm)>();
        using var scores = new Mat();
        foreach (var template in _templates)
        {
            if (_lockedSlot is { } slot && template.Slot != slot) continue;
            if (template.Gray.Width > gray.Width || template.Gray.Height > gray.Height) continue;
            Cv2.MatchTemplate(gray, template.Gray, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var score, out _, out var point);
            if (!double.IsFinite(score) || score < .70) continue;

            var markerCenter = new Point2d(point.X + template.CenterX, point.Y + template.CenterY);
            var distNorm = Math.Sqrt(Math.Pow(markerCenter.X - imgCenter.X, 2) + Math.Pow(markerCenter.Y - imgCenter.Y, 2)) / minDim;

            // 原生小地图自机必定锚定在中心（归一化半径 < 0.20）；偏离中心的必定是队友或边缘背景
            if (distNorm > 0.20) continue;

            if (!candidates.TryGetValue(template.Slot, out var old) || score > old.Score)
                candidates[template.Slot] = (template, point, score, distNorm);
        }

        NativeMiniMapHeading? best = null;
        double runnerUp = 0;
        foreach (var (template, point, score, distNorm) in candidates.Values)
        {
            reason = "cone_not_found";
            var center = new Point2d(point.X + template.CenterX, point.Y + template.CenterY);
            var cone = ReadCone(gray, hsv, center, template.Radius);
            if (cone is null) continue;
            // 结合模板分、视野锥对比度以及中心近邻度计算综合置信度
            var centerBonus = 1.0 - Math.Clamp(distNorm / 0.20, 0.0, 0.20);
            var coneQuality = Math.Clamp(0.40 + (cone.Value.Contrast / 25.0) * 0.60, 0.40, 1.0);
            var confidence = score * coneQuality * centerBonus;
            var result = new NativeMiniMapHeading(template.Slot, cone.Value.Degrees,
                confidence, cone.Value.Contrast, center);
            if (best is null || confidence > best.Confidence)
            {
                runnerUp = best?.Confidence ?? 0;
                best = result;
            }
            else runnerUp = Math.Max(runnerUp, confidence);
        }

        if (best is null)
        {
            if (_lockedSlot is not null) _missingLockedSlotCount++;
            return null;
        }

        // 若尚未锁定槽位且存在两个近邻的模糊候选，保持谨慎；若已锁定自身槽位，绝不退回 ambiguous
        if (_lockedSlot is null && runnerUp > 0 && best.Confidence - runnerUp < .08)
        {
            reason = "ambiguous_player_cones";
            return null;
        }

        _lockedSlot = best.Slot;
        _missingLockedSlotCount = 0;
        reason = "accepted";
        return best;
    }

    private static (double Degrees, double Contrast)? ReadCone(Mat gray, Mat hsv, Point2d center, double radius)
    {
        int width = gray.Width, height = gray.Height;
        if (width <= 0 || height <= 0) return null;

        // 1. Dominant hue of inner disk
        var hues = new int[60];
        int scanR = Math.Max(8, (int)Math.Ceiling(radius * 0.85));
        int minX = Math.Max(0, (int)(center.X - scanR)), maxX = Math.Min(width - 1, (int)(center.X + scanR));
        int minY = Math.Max(0, (int)(center.Y - scanR)), maxY = Math.Min(height - 1, (int)(center.Y + scanR));
        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - center.Y;
            var dy2 = dy * dy;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - center.X;
                var d2 = dx * dx + dy2;
                if (d2 < 16 || d2 > scanR * scanR) continue;
                var p = hsv.At<Vec3b>(y, x);
                if (p.Item1 > 40 && p.Item2 > 50) hues[p.Item0 / 3]++;
            }
        }
        if (hues.Sum() < 10) return null;
        var targetHue = Array.IndexOf(hues, hues.Max()) * 3 + 1;

        // 2. Measure outer radius along 72 angular bins (5° each).
        // Native mini-map markers have an opaque, solid pointer triangle pointing forward.
        // Its prominence is immune to transparent background scenes moving underneath.
        var radii = new double[72];
        double minScanRadius = Math.Max(8.0, radius * 0.85);
        double maxScanRadius = Math.Min(Math.Min(width, height) * 0.5, radius * 2.5);

        for (var b = 0; b < 72; b++)
        {
            var rad = b * 5.0 * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            double lastMatchingR = minScanRadius;
            for (double r = minScanRadius; r <= maxScanRadius; r += 0.5)
            {
                var x = (int)Math.Round(center.X + r * cos);
                var y = (int)Math.Round(center.Y + r * sin);
                if (x < 0 || x >= width || y < 0 || y >= height) break;
                var p = hsv.At<Vec3b>(y, x);
                var deltaH = Math.Abs(p.Item0 - targetHue);
                var isMatch = Math.Min(deltaH, 180 - deltaH) <= 12 && p.Item1 >= 45 && p.Item2 >= 60;
                if (isMatch) lastMatchingR = r;
                else if (r - lastMatchingR > 2.0) break;
            }
            radii[b] = lastMatchingR;
        }

        var sortedR = radii.OrderBy(r => r).ToArray();
        var baseRadius = sortedR[18]; // 25th percentile represents clean disk baseline
        var peakBin = 0;
        var maxPointerR = radii[0];
        for (var b = 1; b < 72; b++)
        {
            if (radii[b] > maxPointerR)
            {
                maxPointerR = radii[b];
                peakBin = b;
            }
        }

        var prominence = maxPointerR - baseRadius;

        // 3. Subpixel refinement of pointer heading
        var prevR = radii[(peakBin + 71) % 72];
        var nextR = radii[(peakBin + 1) % 72];
        var curvature = prevR - 2 * maxPointerR + nextR;
        var offset = curvature < -0.01 ? Math.Clamp(0.5 * (prevR - nextR) / curvature, -0.5, 0.5) : 0;
        var pointerDegrees = ((peakBin + offset) * 5.0 + 90.0) % 360.0;

        // 4. Vision cone evaluation with background suppression in outer ring
        var innerConeR = baseRadius * 1.35;
        var outerConeR = Math.Min(Math.Min(width, height) * 0.5, baseRadius * 2.5);
        var coneGraySums = new double[72];
        var coneGrayCounts = new int[72];

        int cMinX = Math.Max(0, (int)(center.X - outerConeR));
        int cMaxX = Math.Min(width - 1, (int)(center.X + outerConeR));
        int cMinY = Math.Max(0, (int)(center.Y - outerConeR));
        int cMaxY = Math.Min(height - 1, (int)(center.Y + outerConeR));

        for (var y = cMinY; y <= cMaxY; y++)
        {
            var dy = y - center.Y;
            var dy2 = dy * dy;
            for (var x = cMinX; x <= cMaxX; x++)
            {
                var dx = x - center.X;
                var dist = Math.Sqrt(dx * dx + dy2);
                if (dist < innerConeR || dist > outerConeR) continue;
                var bin = (int)((Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) / 5.0) % 72;
                coneGraySums[bin] += gray.At<byte>(y, x);
                coneGrayCounts[bin]++;
            }
        }

        double AverageSector(double centerGameAngle, int halfSpanDeg)
        {
            double sum = 0;
            var cnt = 0;
            for (var d = -halfSpanDeg; d <= halfSpanDeg; d += 5)
            {
                var screenAngle = (centerGameAngle + d - 90.0 + 360.0) % 360.0;
                var b = (int)(screenAngle / 5.0) % 72;
                sum += coneGraySums[b];
                cnt += coneGrayCounts[b];
            }
            return cnt > 0 ? sum / cnt : 0;
        }

        // If strong pointer is detected (prominence >= 3.0px), lock heading to pointer
        // and verify contrast against flanking background to reject dark/obscured cones.
        if (prominence >= 3.0)
        {
            var coneBrightness = AverageSector(pointerDegrees, 20);
            var leftFlank = AverageSector(pointerDegrees - 60, 15);
            var rightFlank = AverageSector(pointerDegrees + 60, 15);
            var flankBackground = Math.Max(leftFlank, rightFlank);
            var coneContrast = Math.Max(prominence * 2.5, coneBrightness - flankBackground);
            return (pointerDegrees, coneContrast);
        }

        // Fallback for cases where pointer is temporarily ambiguous: search cone contrast
        var contrasts = new double[72];
        Array.Fill(contrasts, double.NegativeInfinity);
        for (var k = 0; k < 72; k++)
        {
            var gameAngle = (k * 5.0 + 90.0) % 360.0;
            var centerBright = AverageSector(gameAngle, 15);
            var left = AverageSector(gameAngle - 60, 15);
            var right = AverageSector(gameAngle + 60, 15);
            contrasts[k] = centerBright - Math.Max(left, right);
        }

        var best = -1;
        for (var k = 0; k < 72; k++)
            if (double.IsFinite(contrasts[k]) && contrasts[k] > 8
                && (best < 0 || contrasts[k] > contrasts[best])) best = k;

        if (best < 0) return null;
        var prevC = contrasts[(best + 71) % 72];
        var nextC = contrasts[(best + 1) % 72];
        var curvC = prevC - 2 * contrasts[best] + nextC;
        var offC = curvC < -0.01 ? Math.Clamp(0.5 * (prevC - nextC) / curvC, -0.5, 0.5) : 0;
        return (((best + offC) * 5.0 + 90.0) % 360.0, contrasts[best]);
    }

    public static double ShortestDelta(double from, double to) => ((to - from + 540) % 360 + 360) % 360 - 180;

    /// <summary>
    /// 将连续罗盘朝向角度量化吸附到 8 个离散方位（0° N, 45° NE, 90° E, 135° SE, 180° S, 225° SW, 270° W, 315° NW），
    /// 并带有迟滞回差（Hysteresis），防止在 22.5° 等边界处来回切角抖动。
    /// </summary>
    public static double SnapTo8Directions(double continuousDegrees, double? currentSnapped = null, double hysteresisDegrees = 4.0)
    {
        var normalized = (continuousDegrees % 360.0 + 360.0) % 360.0;
        if (currentSnapped is { } current && double.IsFinite(current))
        {
            var currentNorm = (current % 360.0 + 360.0) % 360.0;
            var delta = Math.Abs(ShortestDelta(currentNorm, normalized));
            if (delta <= 22.5 + hysteresisDegrees)
                return currentNorm;
        }

        var index = (int)Math.Round(normalized / 45.0) % 8;
        return index * 45.0;
    }

    public void Dispose()
    {
        foreach (var template in _templates) template.Gray.Dispose();
        _templates.Clear();
    }
}
