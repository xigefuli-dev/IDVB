using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class MapPlayerMarkerDetection
{
    public bool Succeeded { get; init; }
    public PlayerSlot PlayerSlot { get; init; }
    public MapViewportPoint ViewportPoint { get; init; }
    public MapScreenPoint ScreenPoint { get; init; }
    public Rect LocalBounds { get; init; }
    public double TemplateScore { get; init; }
    public double ColorAgreement { get; init; }
    public double ShapeAgreement { get; init; }
    public double Confidence { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

/// <summary>
/// Detects one selected packaged player marker inside an already-locked native map
/// viewport. It never reads or mutates background alignment state.
/// </summary>
public sealed class MapPlayerMarkerDetector : IDisposable
{
    private Mat? _template;
    private PlayerTemplateProfile? _templateProfile;
    private PlayerColorSignature? _templateColorSignature;
    private string _templatePath = string.Empty;
    private PlayerSlot _templateSlot;
    private int _consecutiveFailures;

    public MapPlayerMarkerDetection Detect(
        Mat liveViewport,
        MapScreenRect viewportBounds,
        MapScreenRect clientBounds,
        PlayerSlot playerSlot,
        string templatePath,
        MapViewportPoint? previousPoint,
        MapPlayerTrackingTuning? tuning = null)
    {
        var trackingTuning = tuning?.Clone() ?? new MapPlayerTrackingTuning();
        trackingTuning.Normalize();
        ArgumentNullException.ThrowIfNull(liveViewport);
        if (liveViewport.Empty()
            || !viewportBounds.IsValid
            || !clientBounds.IsValid
            || !Enum.IsDefined(playerSlot)
            || !File.Exists(templatePath))
        {
            return Failure("玩家序号资源或模板不可用。");
        }

        EnsureTemplate(playerSlot, templatePath);
        if (_template is null
            || _template.Empty()
            || _templateProfile is null)
            return Failure("无法读取玩家图标模板。");

        var search = ResolveSearchRect(
            liveViewport.Size(),
            previousPoint,
            _consecutiveFailures >= trackingTuning.LocalSearchFailureLimit);
        using var liveSearch = new Mat(liveViewport, search);
        using var liveGray = ToGray(liveSearch);

        PlayerCandidate? best = null;
        foreach (var scale in PlayerTrackingRules.TemplateScaleCandidates)
        {
            var width = Math.Max(3, (int)Math.Round(_template.Width * scale));
            var height = Math.Max(3, (int)Math.Round(_template.Height * scale));
            if (width >= liveSearch.Width || height >= liveSearch.Height)
                continue;

            using var resized = new Mat();
            Cv2.Resize(
                _template,
                resized,
                new Size(width, height),
                interpolation: InterpolationFlags.Area);
            using var templateGray = ToGray(resized);
            using var scores = new Mat();
            Cv2.MatchTemplate(
                liveGray,
                templateGray,
                scores,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(
                scores,
                out _,
                out var score,
                out _,
                out var location);
            if (!double.IsFinite(score))
                continue;
            var bounds = new Rect(
                search.X + location.X,
                search.Y + location.Y,
                width,
                height);
            var visualAgreement = CalculateVisualAgreement(
                liveViewport,
                bounds,
                _templateProfile,
                _templateColorSignature);
            var confidence = Math.Clamp(
                (Math.Max(0d, score) * PlayerTrackingRules.TemplateScoreWeight)
                + (visualAgreement.Color * PlayerTrackingRules.ColorAgreementWeight)
                + (visualAgreement.Shape * PlayerTrackingRules.ShapeAgreementWeight),
                0d,
                1d);
            var candidate = new PlayerCandidate(
                bounds,
                score,
                visualAgreement.Color,
                visualAgreement.Shape,
                confidence);
            if (best is null || candidate.Confidence > best.Confidence)
                best = candidate;
        }

        if (best is null
            || best.TemplateScore < PlayerTrackingRules.MinimumTemplateScore
            || best.ColorAgreement < PlayerTrackingRules.MinimumColorAgreement
            || best.Confidence < trackingTuning.MinimumConfidence)
        {
            _consecutiveFailures++;
            return Failure(
                best is null
                    ? "玩家图标模板大于搜索区域。"
                    : $"玩家图标置信度 {best.Confidence:P0} 不足。");
        }

        _consecutiveFailures = 0;
        var viewportPoint = new MapViewportPoint(
            best.Bounds.X + (best.Bounds.Width / 2d),
            best.Bounds.Y + (best.Bounds.Height / 2d));
        return new MapPlayerMarkerDetection
        {
            Succeeded = true,
            PlayerSlot = playerSlot,
            ViewportPoint = viewportPoint,
            ScreenPoint = new MapScreenPoint(
                viewportBounds.X + viewportPoint.X,
                viewportBounds.Y + viewportPoint.Y),
            LocalBounds = best.Bounds,
            TemplateScore = best.TemplateScore,
            ColorAgreement = best.ColorAgreement,
            ShapeAgreement = best.ShapeAgreement,
            Confidence = best.Confidence
        };
    }

    private static PlayerTemplateProfile AnalyzeTemplate(Mat template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Empty())
            throw new ArgumentException("玩家图标模板不能为空。", nameof(template));

        using var bgr = ToBgr(template);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        try
        {
            Cv2.MeanStdDev(channels[0], out var hueMean, out var hueStd);
            Cv2.MeanStdDev(
                channels[1],
                out var saturationMean,
                out var saturationStd);
            Cv2.MeanStdDev(channels[2], out var valueMean, out var valueStd);
            var profile = new PlayerTemplateProfile
            {
                MinimumHue = Math.Clamp(
                    hueMean.Val0 - (hueStd.Val0 * 2d),
                    0d,
                    179d),
                MaximumHue = Math.Clamp(
                    hueMean.Val0 + (hueStd.Val0 * 2d),
                    0d,
                    179d),
                MinimumSaturation = Math.Clamp(
                    saturationMean.Val0 - (saturationStd.Val0 * 2d),
                    0d,
                    255d),
                MinimumValue = Math.Clamp(
                    valueMean.Val0 - (valueStd.Val0 * 2d),
                    0d,
                    255d)
            };
            var metrics = MeasureMarkerShape(template, profile);
            profile.ExpectedColorAreaRatio = metrics.AreaRatio;
            profile.ExpectedContourFillRatio = metrics.ContourFill;
            return profile;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    public void ResetTracking() => _consecutiveFailures = 0;

    private void EnsureTemplate(PlayerSlot playerSlot, string templatePath)
    {
        var fullPath = Path.GetFullPath(templatePath);
        if (_template is not null
            && string.Equals(
                _templatePath,
                fullPath,
                StringComparison.OrdinalIgnoreCase)
            && _templateSlot == playerSlot)
        {
            return;
        }
        _template?.Dispose();
        _template = Cv2.ImRead(fullPath, ImreadModes.Unchanged);
        _templateProfile = _template.Empty()
            ? null
            : AnalyzeTemplate(
                _template);
        _templateColorSignature = _template.Empty()
            ? null
            : CalculateColorSignature(_template);
        _templatePath = fullPath;
        _templateSlot = playerSlot;
        _consecutiveFailures = 0;
    }

    private static Rect ResolveSearchRect(
        Size size,
        MapViewportPoint? previousPoint,
        bool forceGlobal)
    {
        if (forceGlobal || previousPoint is not { } point || !point.IsFinite)
            return new Rect(0, 0, size.Width, size.Height);
        var radius = Math.Max(
            48,
            (int)Math.Round(Math.Max(size.Width, size.Height) * 0.12d));
        var left = Math.Clamp(
            (int)Math.Round(point.X) - radius,
            0,
            Math.Max(0, size.Width - 1));
        var top = Math.Clamp(
            (int)Math.Round(point.Y) - radius,
            0,
            Math.Max(0, size.Height - 1));
        var right = Math.Clamp(
            (int)Math.Round(point.X) + radius + 1,
            left + 1,
            size.Width);
        var bottom = Math.Clamp(
            (int)Math.Round(point.Y) + radius + 1,
            top + 1,
            size.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static (double Color, double Shape) CalculateVisualAgreement(
        Mat source,
        Rect bounds,
        PlayerTemplateProfile profile,
        PlayerColorSignature? expectedColor)
    {
        using var patch = new Mat(source, bounds);
        var measured = MeasureMarkerShape(patch, profile);
        var color = expectedColor is { } expected
            && CalculateColorSignature(patch) is { } actual
                ? ColorAgreement(expected, actual)
                : 0d;
        var shape = Agreement(
            measured.ContourFill,
            profile.ExpectedContourFillRatio,
            minimumTolerance: PlayerTrackingRules.MinimumShapeTolerance);
        return (color, shape);
    }

    private static PlayerColorSignature? CalculateColorSignature(Mat source)
    {
        using var bgr = ToBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        double sine = 0d;
        double cosine = 0d;
        double saturation = 0d;
        double value = 0d;
        double totalWeight = 0d;
        var height = hsv.Height;
        var width = hsv.Width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = hsv.At<Vec3b>(y, x);
                if (pixel.Item1 < 40 || pixel.Item2 < 40)
                    continue;
                var weight =
                    (pixel.Item1 / 255d) * (pixel.Item2 / 255d);
                var radians = pixel.Item0 * Math.PI / 90d;
                sine += Math.Sin(radians) * weight;
                cosine += Math.Cos(radians) * weight;
                saturation += pixel.Item1 * weight;
                value += pixel.Item2 * weight;
                totalWeight += weight;
            }
        }
        if (totalWeight <= 0d)
            return null;
        var hue = Math.Atan2(sine, cosine) * 90d / Math.PI;
        if (hue < 0d)
            hue += 180d;
        return new PlayerColorSignature(
            hue,
            saturation / totalWeight,
            value / totalWeight);
    }

    private static double ColorAgreement(
        PlayerColorSignature expected,
        PlayerColorSignature actual)
    {
        var directHue = Math.Abs(expected.Hue - actual.Hue);
        var hueDistance = Math.Min(directHue, 180d - directHue);
        var hue = 1d - Math.Clamp(hueDistance / 25d, 0d, 1d);
        var saturation = 1d - Math.Clamp(
            Math.Abs(expected.Saturation - actual.Saturation) / 160d,
            0d,
            1d);
        var value = 1d - Math.Clamp(
            Math.Abs(expected.Value - actual.Value) / 200d,
            0d,
            1d);
        return Math.Clamp(
            (hue * 0.70d) + (saturation * 0.20d) + (value * 0.10d),
            0d,
            1d);
    }

    private static (double AreaRatio, double ContourFill) MeasureMarkerShape(
        Mat source,
        PlayerTemplateProfile profile)
    {
        using var bgr = ToBgr(source);
        using var hsv = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(
            hsv,
            new Scalar(
                profile.MinimumHue,
                profile.MinimumSaturation,
                profile.MinimumValue),
            new Scalar(profile.MaximumHue, 255d, 255d),
            mask);
        var areaRatio = Cv2.CountNonZero(mask)
            / (double)Math.Max(1, mask.Width * mask.Height);
        Cv2.FindContours(
            mask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);
        var largest = contours
            .OrderByDescending(contour => Cv2.ContourArea(contour))
            .FirstOrDefault();
        if (largest is null || largest.Length < 3)
            return (areaRatio, 0d);
        var contourArea = Cv2.ContourArea(largest);
        var contourBounds = Cv2.BoundingRect(largest);
        var fill = contourArea
            / Math.Max(1d, contourBounds.Width * contourBounds.Height);
        return (
            Math.Clamp(areaRatio, 0.01d, 1d),
            Math.Clamp(fill, 0.01d, 1d));
    }

    private static double Agreement(
        double measured,
        double expected,
        double minimumTolerance)
    {
        var tolerance = Math.Max(
            minimumTolerance,
            Math.Abs(expected) * 0.60d);
        return 1d - Math.Clamp(
            Math.Abs(measured - expected) / tolerance,
            0d,
            1d);
    }

    private MapPlayerMarkerDetection Failure(string reason) =>
        new()
        {
            FailureReason = reason
        };

    private static Mat ToGray(Mat source)
    {
        var gray = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            case 3:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                break;
            default:
                source.CopyTo(gray);
                break;
        }
        return gray;
    }

    private static Mat ToBgr(Mat source)
    {
        var bgr = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                source.CopyTo(bgr);
                break;
            default:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
        }
        return bgr;
    }

    public void Dispose()
    {
        _template?.Dispose();
        _template = null;
        _templateProfile = null;
        _templateColorSignature = null;
        _templatePath = string.Empty;
        _templateSlot = default;
    }

    private sealed record PlayerCandidate(
        Rect Bounds,
        double TemplateScore,
        double ColorAgreement,
        double ShapeAgreement,
        double Confidence);

    private sealed class PlayerTemplateProfile
    {
        public double MinimumHue { get; init; }
        public double MaximumHue { get; init; } = 179d;
        public double MinimumSaturation { get; init; }
        public double MinimumValue { get; init; }
        public double ExpectedColorAreaRatio { get; set; } = 0.25d;
        public double ExpectedContourFillRatio { get; set; } = 0.50d;
    }

    private readonly record struct PlayerColorSignature(
        double Hue,
        double Saturation,
        double Value);
}
/*
 * 文件职责：MapPlayerMarkerDetector。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
