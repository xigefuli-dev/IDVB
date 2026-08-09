using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public enum MapScaleSearchPolicy
{
    Fixed,
    Search
}

public sealed class MapStructureRegistrationRequest
{
    private MapScaleSearchPolicy _scaleSearchPolicy;

    public Mat ReferenceImage { get; init; } = new();
    public Mat LiveRoi { get; init; } = new();
    public MapScreenRect ViewportBounds { get; init; }
    public MapOverlayTransform LockedTransform { get; init; } = new();
    public MapStructureRegistrationTuning Tuning { get; init; } = new();
    public MapScaleSearchPolicy ScaleSearchPolicy
    {
        get => _scaleSearchPolicy;
        init => _scaleSearchPolicy = value;
    }

    // Compatibility shim for older callers and persisted probe code. New
    // production paths use ScaleSearchPolicy explicitly.
    public bool AllowScaleSearch
    {
        get => _scaleSearchPolicy == MapScaleSearchPolicy.Search;
        init => _scaleSearchPolicy = value
            ? MapScaleSearchPolicy.Search
            : MapScaleSearchPolicy.Fixed;
    }
    public bool RestrictSearchToLockedTransform { get; init; }
    public bool TrackingMode { get; init; }
    public bool ForceBestCandidate { get; init; }
    public double FixedRotationDegrees { get; init; }
    public MapReferenceBounds? ValidMapBounds { get; init; }
    public MapViewportOrigin? PredictedViewportOrigin { get; init; }
    public MapReferencePoint? PlayerPrior { get; init; }
    public IReadOnlyList<MapSimilarityTransform> CandidateHistory { get; init; } = [];
    public IReadOnlyList<NormalizedRectangle> LiveIgnoreRegions { get; init; } = [];
    public IReadOnlyList<Rect> DynamicIgnoreRegions { get; init; } = [];
    public string? DebugOutputDirectory { get; init; }
    public MapStructureFeatures? PreparedReference { get; init; }
    public MapStructureFeatures? PreparedLive { get; init; }
    /// <summary>
    /// 侧门扫描先验置信度。当前生产调用点一律传入 0：先验只用于会话层的
    /// 身份门控（<c>MapAlignmentSession.SideEntranceScanPriorConfidence</c>），
    /// 不再提升结构配准置信度，位置须由结构证据独立支撑。保留该字段与
    /// 计算器融合逻辑，以便将来恢复融合策略时无需改动请求模型。
    /// </summary>
    public double SideEntrancePrior { get; init; }
}
