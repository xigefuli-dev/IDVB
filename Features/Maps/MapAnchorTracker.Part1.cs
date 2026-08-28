using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Tracks one already-selected map without performing catalog-wide identity
/// ranking. All degraded tracking uses the scale locked by a prior gate pair.
/// </summary>
public static partial class MapAnchorTracker
{

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
}
