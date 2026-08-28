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
    public MapAlignmentChannel Channel { get; init; } = MapAlignmentChannel.Standard;
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
    /// Low-structure callers provide an explicit bounded plan. Standard
    /// registration leaves this null and keeps its established policy.
    /// </summary>
    internal LowStructureAlignmentPlan? LowStructurePlan { get; init; }
    /// <summary>
    /// 侧门扫描先验置信度。当前生产调用点一律传入 0：先验只用于会话层的
    /// 身份门控（<c>MapAlignmentSession.SideEntranceScanPriorConfidence</c>），
    /// 不再提升结构配准置信度，位置须由结构证据独立支撑。保留该字段与
    /// 计算器融合逻辑，以便将来恢复融合策略时无需改动请求模型。
    /// </summary>
    public double SideEntrancePrior { get; init; }
}
/*
 * 文件职责：MapStructureRegistrationModels.Request。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
