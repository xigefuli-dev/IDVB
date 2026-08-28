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
public static partial class MapAnchorTracker
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
}
/*
 * 文件职责：MapAnchorTracker。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
