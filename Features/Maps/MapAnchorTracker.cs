using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed class MapAuxiliaryAnchorTemplateCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Mat> _scaledTemplates = [];
    private Guid _mapId;
    private DateTimeOffset _mapUpdatedAt;
    private int _referenceWidth;
    private int _referenceHeight;
    private Mat? _referenceEdges;
    private bool _disposed;

    public int CachedTemplateCount
    {
        get
        {
            lock (_gate)
                return _scaledTemplates.Count;
        }
    }

    public Mat GetOrCreate(
        Mat referenceImage,
        MapGeometryFingerprint fingerprint,
        RecognitionAnchor anchor,
        Rect referenceRect,
        Size liveTemplateSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_mapId != fingerprint.Map.Id
                || _mapUpdatedAt != fingerprint.Map.UpdatedAt
                || _referenceWidth != referenceImage.Width
                || _referenceHeight != referenceImage.Height)
            {
                ClearCore();
                _mapId = fingerprint.Map.Id;
                _mapUpdatedAt = fingerprint.Map.UpdatedAt;
                _referenceWidth = referenceImage.Width;
                _referenceHeight = referenceImage.Height;
            }

            _referenceEdges ??=
                GateTemplateDetector.CreateEdges(referenceImage);
            var key =
                $"{anchor.Id:N}:{referenceRect.X}:{referenceRect.Y}:"
                + $"{referenceRect.Width}:{referenceRect.Height}:"
                + $"{liveTemplateSize.Width}:{liveTemplateSize.Height}";
            if (_scaledTemplates.TryGetValue(key, out var cached))
                return cached;

            using var patch = new Mat(_referenceEdges, referenceRect);
            var scaled = new Mat();
            Cv2.Resize(
                patch,
                scaled,
                liveTemplateSize,
                0d,
                0d,
                InterpolationFlags.Area);
            _scaledTemplates[key] = scaled;
            return scaled;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ClearCore();
        }
    }

    private void ClearCore()
    {
        foreach (var template in _scaledTemplates.Values)
            template.Dispose();
        _scaledTemplates.Clear();
        _referenceEdges?.Dispose();
        _referenceEdges = null;
    }
}

public sealed class MapAuxiliaryTrackingResult
{
    public bool IsSuccess { get; init; }
    public IReadOnlyList<CvAnchorEvidence> Matches { get; init; } = [];
    public double Confidence { get; init; }
    public double SearchMilliseconds { get; init; }
    public bool UsedGlobalSearch { get; init; }
    public int TemplatesEvaluated { get; init; }
    public string FailureReason { get; init; } = string.Empty;

    public bool HasIndependentConsensus => IsSuccess && Matches.Count >= 2;
}

/// <summary>
/// Tracks one already-selected map without performing catalog-wide identity
/// ranking. All degraded tracking uses the scale locked by a prior gate pair.
/// </summary>
public static class MapAnchorTracker
{
    private const int MinimumTemplatePixels = 12;
    private const double MinimumAuxiliaryScore = 0.78d;
    private const double MinimumGateContextScore = 0.55d;
    private const double ConsensusViewportRatio = 0.005d;
    private const double MinimumConsensusPixels = 6d;

    public static bool TryResolveSingleGate(
        Mat referenceImage,
        Mat liveImage,
        MapGeometryFingerprint fingerprint,
        GateDetection gate,
        MapScreenRect viewportBounds,
        MapOverlayTransform lockedTransform,
        double minimumConfidence,
        double minimumAdvantage,
        out CvAnchorEvidence evidence,
        out string failureReason)
    {
        evidence = new CvAnchorEvidence();
        failureReason = string.Empty;
        if (referenceImage.Empty() || liveImage.Empty() || !viewportBounds.IsValid)
        {
            failureReason = "单门跟踪缺少有效的参考图或实时地图画面。";
            return false;
        }

        using var referenceEdges = GateTemplateDetector.CreateEdges(referenceImage);
        using var liveEdges = GateTemplateDetector.CreateEdges(liveImage);
        var profile = MapFloorRules.GetFloorProfile(
            fingerprint.Map,
            fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor;
        var anchors = new[]
        {
            profile.FindAnchor("main-entrance"),
            profile.FindAnchor("side-entrance")
        };
        var scored = new List<(RecognitionAnchor Anchor, double Score)>();
        foreach (var anchor in anchors)
        {
            if (anchor?.Bounds?.IsValid is not true)
                continue;
            var referenceBounds = ToReferenceBounds(
                anchor.Bounds,
                fingerprint.ReferenceWidth,
                fingerprint.ReferenceHeight);
            var referenceWidth = (int)Math.Clamp(
                Math.Round(referenceBounds.Width * 3d),
                48d,
                180d);
            var referenceHeight = (int)Math.Clamp(
                Math.Round(referenceBounds.Height * 3d),
                48d,
                180d);
            var liveWidth = Math.Max(
                MinimumTemplatePixels,
                (int)Math.Round(referenceWidth * lockedTransform.ScaleX));
            var liveHeight = Math.Max(
                MinimumTemplatePixels,
                (int)Math.Round(referenceHeight * lockedTransform.ScaleY));
            if (!TryExtractCenteredPatch(
                    referenceEdges,
                    referenceBounds.CenterX,
                    referenceBounds.CenterY,
                    referenceWidth,
                    referenceHeight,
                    out var referencePatch)
                || !TryExtractCenteredPatch(
                    liveEdges,
                    gate.ScreenBounds.CenterX - viewportBounds.X,
                    gate.ScreenBounds.CenterY - viewportBounds.Y,
                    liveWidth,
                    liveHeight,
                    out var livePatch))
            {
                continue;
            }

            using (referencePatch)
            using (livePatch)
            using (var resizedReference = new Mat())
            {
                Cv2.Resize(
                    referencePatch,
                    resizedReference,
                    livePatch.Size(),
                    0d,
                    0d,
                    InterpolationFlags.Area);
                scored.Add((anchor, CosineSimilarity(resizedReference, livePatch)));
            }
        }

        var ranked = scored
            .OrderByDescending(item => item.Score)
            .ToArray();
        var requiredScore = Math.Max(
            MinimumGateContextScore,
            NormalizeThreshold(minimumConfidence, MinimumGateContextScore));
        var requiredAdvantage = NormalizeThreshold(minimumAdvantage, 0.08d);
        if (ranked.Length < 2
            || ranked[0].Score < requiredScore
            || ranked[0].Score - ranked[1].Score < requiredAdvantage)
        {
            failureReason =
                "只看到一扇门，但门周围纹理不足以可靠区分大门和侧门。";
            return false;
        }

        var winner = ranked[0];
        evidence = new CvAnchorEvidence
        {
            AnchorId = winner.Anchor.Id,
            Score = Math.Clamp((winner.Score + gate.Score) / 2d, 0d, 1d),
            TemplateScale = gate.Scale,
            ReferenceBounds = ToReferenceBounds(
                winner.Anchor.Bounds!,
                fingerprint.ReferenceWidth,
                fingerprint.ReferenceHeight),
            ScreenBounds = gate.ScreenBounds
        };
        return true;
    }

    public static MapAuxiliaryTrackingResult TrackAuxiliaryAnchors(
        Mat referenceImage,
        Mat liveImage,
        MapGeometryFingerprint fingerprint,
        MapScreenRect viewportBounds,
        MapOverlayTransform lockedTransform,
        double minimumScore,
        double minimumAdvantage,
        int maximumTemplates = 4,
        MapAuxiliaryAnchorTemplateCache? templateCache = null)
    {
        var stopwatch = Stopwatch.StartNew();
        if (referenceImage.Empty() || liveImage.Empty() || !viewportBounds.IsValid)
        {
            return Failure(
                "辅助锚点跟踪缺少有效的参考图或实时地图画面。",
                stopwatch);
        }

        using var ownedReferenceEdges = templateCache is null
            ? GateTemplateDetector.CreateEdges(referenceImage)
            : null;
        var referenceEdges = ownedReferenceEdges;
        using var liveEdges = GateTemplateDetector.CreateEdges(liveImage);
        var requiredScore = Math.Max(
            MinimumAuxiliaryScore,
            NormalizeThreshold(minimumScore, MinimumAuxiliaryScore));
        var requiredAdvantage = NormalizeThreshold(minimumAdvantage, 0.08d);
        var candidates = new List<OffsetCandidate>();
        var evaluated = 0;
        var usedGlobalSearch = false;
        var anchors = (MapFloorRules.GetFloorProfile(
                fingerprint.Map,
                fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor).Anchors
            .Where(anchor => anchor.Role == RecognitionAnchorRole.Optional
                && anchor.Bounds?.IsValid is true)
            .Select(anchor => new
            {
                Anchor = anchor,
                Bounds = ToReferenceBounds(
                    anchor.Bounds!,
                    fingerprint.ReferenceWidth,
                    fingerprint.ReferenceHeight)
            })
            .OrderByDescending(item => IsPredictedVisible(
                item.Bounds,
                viewportBounds,
                lockedTransform))
            .ThenByDescending(item => item.Anchor.Weight)
            .ThenByDescending(item =>
                item.Bounds.Width * item.Bounds.Height)
            .Take(Math.Clamp(maximumTemplates, 1, 8))
            .ToArray();
        foreach (var item in anchors)
        {
            var anchor = item.Anchor;
            var referenceBounds = item.Bounds;
            if (!IsPredictedVisible(referenceBounds, viewportBounds, lockedTransform)
                && candidates.Count >= 2)
            {
                continue;
            }
            var referenceRect = ToClampedRect(
                referenceBounds,
                referenceImage.Width,
                referenceImage.Height);
            if (referenceRect.Width < MinimumTemplatePixels
                || referenceRect.Height < MinimumTemplatePixels)
            {
                continue;
            }

            var liveWidth = Math.Max(
                MinimumTemplatePixels,
                (int)Math.Round(referenceRect.Width * lockedTransform.ScaleX));
            var liveHeight = Math.Max(
                MinimumTemplatePixels,
                (int)Math.Round(referenceRect.Height * lockedTransform.ScaleY));
            if (liveWidth >= liveEdges.Width || liveHeight >= liveEdges.Height)
                continue;
            Mat? ownedScaledTemplate = null;
            var scaledTemplate = templateCache?.GetOrCreate(
                referenceImage,
                fingerprint,
                anchor,
                referenceRect,
                new Size(liveWidth, liveHeight));
            if (scaledTemplate is null)
            {
                using var referencePatch = new Mat(
                    referenceEdges!,
                    referenceRect);
                ownedScaledTemplate = new Mat();
                Cv2.Resize(
                    referencePatch,
                    ownedScaledTemplate,
                    new Size(liveWidth, liveHeight),
                    0d,
                    0d,
                    InterpolationFlags.Area);
                scaledTemplate = ownedScaledTemplate;
            }
            try
            {
                if (Cv2.CountNonZero(scaledTemplate) == 0)
                    continue;

                evaluated++;
                // Use full-domain search for global uniqueness so a
                // repeated room texture elsewhere cannot masquerade as
                // the predicted anchor.
                usedGlobalSearch = true;
                var matched = TryMatchTemplate(
                    liveEdges,
                    scaledTemplate,
                    new Rect(
                        0,
                        0,
                        liveEdges.Width - scaledTemplate.Width + 1,
                        liveEdges.Height - scaledTemplate.Height + 1),
                    requiredScore,
                    requiredAdvantage,
                    out var bestScore,
                    out var secondScore,
                    out var bestLocation);
                if (bestScore < requiredScore
                    || bestScore - secondScore < requiredAdvantage
                    || !matched)
                {
                    continue;
                }

                var screenBounds = new MapScreenRect(
                    viewportBounds.X + bestLocation.X,
                    viewportBounds.Y + bestLocation.Y,
                    liveWidth,
                    liveHeight);
                var evidence = new CvAnchorEvidence
                {
                    AnchorId = anchor.Id,
                    Score = bestScore,
                    ReferenceBounds = referenceBounds,
                    ScreenBounds = screenBounds
                };
                candidates.Add(new OffsetCandidate(
                    evidence,
                    screenBounds.CenterX
                        - (referenceBounds.CenterX * lockedTransform.ScaleX),
                    screenBounds.CenterY
                        - (referenceBounds.CenterY * lockedTransform.ScaleY)));
                if (HasPreliminaryIndependentConsensus(
                        candidates,
                        fingerprint,
                        viewportBounds))
                {
                    break;
                }
            }
            finally
            {
                ownedScaledTemplate?.Dispose();
            }
        }

        if (candidates.Count == 0)
        {
            return Failure(
                "没有找到高置信且唯一的辅助锚点；可能锚点已离开显示边界或地图缩放发生变化。",
                stopwatch,
                evaluated,
                usedGlobalSearch);
        }
        if (candidates.Count == 1)
        {
            stopwatch.Stop();
            return new MapAuxiliaryTrackingResult
            {
                IsSuccess = true,
                Matches = [candidates[0].Evidence],
                Confidence = candidates[0].Evidence.Score * 0.80d,
                SearchMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                UsedGlobalSearch = usedGlobalSearch,
                TemplatesEvaluated = evaluated
            };
        }

        var consensusTolerance = Math.Max(
            MinimumConsensusPixels,
            Math.Sqrt(
                (viewportBounds.Width * viewportBounds.Width)
                + (viewportBounds.Height * viewportBounds.Height))
            * ConsensusViewportRatio);
        var clusters = candidates
            .Select(seed => candidates.Where(candidate =>
                    Distance(seed.OffsetX, seed.OffsetY, candidate.OffsetX, candidate.OffsetY)
                        <= consensusTolerance)
                .ToArray())
            .DistinctBy(cluster => string.Join(
                "|",
                cluster
                    .Select(item => item.Evidence.AnchorId)
                    .OrderBy(id => id)))
            .OrderByDescending(cluster => cluster.Length)
            .ThenByDescending(cluster => cluster.Average(item => item.Evidence.Score))
            .ToArray();
        var consensus = clusters[0];
        if (consensus.Length < 2)
        {
            return Failure(
                "多个辅助锚点给出了不一致的地图位移，已拒绝更新覆盖层。",
                stopwatch,
                evaluated,
                usedGlobalSearch);
        }
        if (clusters.Length > 1
            && clusters[1].Length == consensus.Length
            && Math.Abs(
                consensus.Average(item => item.Evidence.Score)
                - clusters[1].Average(item => item.Evidence.Score))
                < requiredAdvantage)
        {
            return Failure(
                "辅助锚点形成了两个同样可信的位移结果，已拒绝更新覆盖层。",
                stopwatch,
                evaluated,
                usedGlobalSearch);
        }

        var referenceDiagonal = Math.Sqrt(
            (fingerprint.ReferenceWidth * fingerprint.ReferenceWidth)
            + (fingerprint.ReferenceHeight * fingerprint.ReferenceHeight));
        var independent = consensus
            .Where(left => consensus.Any(right =>
                left.Evidence.AnchorId != right.Evidence.AnchorId
                && Distance(
                    left.Evidence.ReferenceBounds.CenterX,
                    left.Evidence.ReferenceBounds.CenterY,
                    right.Evidence.ReferenceBounds.CenterX,
                    right.Evidence.ReferenceBounds.CenterY)
                    >= referenceDiagonal * 0.05d))
            .ToArray();
        if (independent.Length < 2)
        {
            return Failure(
                "辅助锚点过于接近，不能作为两份独立定位证据。",
                stopwatch,
                evaluated,
                usedGlobalSearch);
        }

        var maximumOffsetSpread = independent.Max(left =>
            independent.Max(right => Distance(
                left.OffsetX,
                left.OffsetY,
                right.OffsetX,
                right.OffsetY)));
        var consensusQuality = Math.Clamp(
            1d - (maximumOffsetSpread / Math.Max(1d, consensusTolerance)),
            0d,
            1d);
        var matches = independent
            .Select(candidate => candidate.Evidence)
            .DistinctBy(evidence => evidence.AnchorId)
            .ToArray();
        var averageScore = matches.Average(match => match.Score);
        var minimumMatchScore = matches.Min(match => match.Score);
        var confidence = Math.Clamp(
            (averageScore * 0.65d)
            + (minimumMatchScore * 0.20d)
            + (consensusQuality * 0.15d),
            0d,
            1d);
        stopwatch.Stop();
        return new MapAuxiliaryTrackingResult
        {
            IsSuccess = true,
            Matches = matches,
            Confidence = confidence,
            SearchMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            UsedGlobalSearch = usedGlobalSearch,
            TemplatesEvaluated = evaluated
        };
    }

    private sealed record OffsetCandidate(
        CvAnchorEvidence Evidence,
        double OffsetX,
        double OffsetY);

    private static MapAuxiliaryTrackingResult Failure(
        string reason,
        Stopwatch stopwatch,
        int templatesEvaluated = 0,
        bool usedGlobalSearch = false)
    {
        stopwatch.Stop();
        return new MapAuxiliaryTrackingResult
        {
            FailureReason = reason,
            SearchMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            TemplatesEvaluated = templatesEvaluated,
            UsedGlobalSearch = usedGlobalSearch
        };
    }

    private static bool IsPredictedVisible(
        MapScreenRect referenceBounds,
        MapScreenRect viewportBounds,
        MapOverlayTransform transform)
    {
        var left = (referenceBounds.X * transform.ScaleX)
            + transform.OffsetX;
        var top = (referenceBounds.Y * transform.ScaleY)
            + transform.OffsetY;
        var right = left + (referenceBounds.Width * transform.ScaleX);
        var bottom = top + (referenceBounds.Height * transform.ScaleY);
        return right >= viewportBounds.X
            && bottom >= viewportBounds.Y
            && left <= viewportBounds.X + viewportBounds.Width
            && top <= viewportBounds.Y + viewportBounds.Height;
    }

    private static bool TryMatchTemplate(
        Mat image,
        Mat template,
        Rect domain,
        double requiredScore,
        double requiredAdvantage,
        out double bestScore,
        out double secondScore,
        out Point bestLocation)
    {
        bestScore = 0d;
        secondScore = 0d;
        bestLocation = default;
        if (domain.Width <= 0
            || domain.Height <= 0
            || domain.Right + template.Width - 1 > image.Width
            || domain.Bottom + template.Height - 1 > image.Height)
        {
            return false;
        }

        using var source = new Mat(
            image,
            new Rect(
                domain.X,
                domain.Y,
                domain.Width + template.Width - 1,
                domain.Height + template.Height - 1));
        using var scores = new Mat();
        Cv2.MatchTemplate(
            source,
            template,
            scores,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(
            scores,
            out _,
            out bestScore,
            out _,
            out var localBest);
        bestLocation = new Point(
            domain.X + localBest.X,
            domain.Y + localBest.Y);
        using var suppressed = scores.Clone();
        Cv2.Rectangle(
            suppressed,
            CreateSuppressionRect(
                localBest,
                template.Size(),
                suppressed.Size()),
            Scalar.All(-1d),
            -1);
        Cv2.MinMaxLoc(
            suppressed,
            out _,
            out secondScore,
            out _,
            out _);
        return bestScore >= requiredScore
            && bestScore - secondScore >= requiredAdvantage;
    }

    private static MapScreenRect ToReferenceBounds(
        NormalizedRectangle bounds,
        int width,
        int height) =>
        new(
            bounds.X * width,
            bounds.Y * height,
            bounds.Width * width,
            bounds.Height * height);

    private static Rect ToClampedRect(
        MapScreenRect bounds,
        int imageWidth,
        int imageHeight)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling(bounds.X + bounds.Width),
            left + 1,
            imageWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(bounds.Y + bounds.Height),
            top + 1,
            imageHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool TryExtractCenteredPatch(
        Mat image,
        double centerX,
        double centerY,
        int width,
        int height,
        out Mat patch)
    {
        patch = new Mat();
        if (width < MinimumTemplatePixels
            || height < MinimumTemplatePixels
            || width > image.Width
            || height > image.Height)
        {
            return false;
        }
        var left = (int)Math.Round(centerX - (width / 2d));
        var top = (int)Math.Round(centerY - (height / 2d));
        left = Math.Clamp(left, 0, image.Width - width);
        top = Math.Clamp(top, 0, image.Height - height);
        patch = new Mat(image, new Rect(left, top, width, height)).Clone();
        return true;
    }

    private static Rect CreateSuppressionRect(Point location, Size template, Size output)
    {
        var left = Math.Max(0, location.X - (template.Width / 2));
        var top = Math.Max(0, location.Y - (template.Height / 2));
        var right = Math.Min(output.Width, location.X + template.Width);
        var bottom = Math.Min(output.Height, location.Y + template.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double CosineSimilarity(Mat left, Mat right)
    {
        using var leftFloat = new Mat();
        using var rightFloat = new Mat();
        left.ConvertTo(leftFloat, MatType.CV_32FC1);
        right.ConvertTo(rightFloat, MatType.CV_32FC1);
        var denominator = Cv2.Norm(leftFloat) * Cv2.Norm(rightFloat);
        return denominator <= 0.000001d
            ? 0d
            : Math.Clamp(leftFloat.Dot(rightFloat) / denominator, 0d, 1d);
    }

    private static double NormalizeThreshold(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : fallback;

    private static bool HasPreliminaryIndependentConsensus(
        IReadOnlyList<OffsetCandidate> candidates,
        MapGeometryFingerprint fingerprint,
        MapScreenRect viewportBounds)
    {
        if (candidates.Count < 2)
            return false;
        var offsetTolerance = Math.Max(
            MinimumConsensusPixels,
            Math.Sqrt(
                (viewportBounds.Width * viewportBounds.Width)
                + (viewportBounds.Height * viewportBounds.Height))
            * ConsensusViewportRatio);
        var referenceDistance = Math.Sqrt(
            (fingerprint.ReferenceWidth * fingerprint.ReferenceWidth)
            + (fingerprint.ReferenceHeight * fingerprint.ReferenceHeight))
            * 0.05d;
        for (var leftIndex = 0;
             leftIndex < candidates.Count - 1;
             leftIndex++)
        {
            for (var rightIndex = leftIndex + 1;
                 rightIndex < candidates.Count;
                 rightIndex++)
            {
                var left = candidates[leftIndex];
                var right = candidates[rightIndex];
                if (Distance(
                        left.OffsetX,
                        left.OffsetY,
                        right.OffsetX,
                        right.OffsetY) <= offsetTolerance
                    && Distance(
                        left.Evidence.ReferenceBounds.CenterX,
                        left.Evidence.ReferenceBounds.CenterY,
                        right.Evidence.ReferenceBounds.CenterX,
                        right.Evidence.ReferenceBounds.CenterY)
                        >= referenceDistance)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static double Distance(
        double leftX,
        double leftY,
        double rightX,
        double rightY)
    {
        var deltaX = rightX - leftX;
        var deltaY = rightY - leftY;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }
}
/*
 * 文件职责：MapAnchorTracker。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
