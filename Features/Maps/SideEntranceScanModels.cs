namespace IDVBuff.Features.Maps;

public enum SideEntranceCandidateDisposition
{
    Reliable,
    NeedsVerification,
    Rejected
}

public enum SideEntranceRejectionReason
{
    None,
    WeakTemplateSimilarity,
    AmbiguousTemplateRanking,
    GateSpatialMismatch,
    ScaleAtSearchBoundary,
    InvalidFeatureData,
    StructureRejected,
    IdentityConfidenceTooLow
}

public enum SideEntranceGateAssociationKind
{
    None,
    DetectedGate,
    TemplateOnlyRescue
}

/// <summary>侧门扫描的单条证据结果。模板相似度本身不是地图置信度。</summary>
public sealed class SideEntranceScanCandidate
{
    public MapRecord Map { get; init; } = new();
    public string FloorKey { get; init; } = string.Empty;
    public double MatchScore { get; init; }
    public MapScreenRect MatchLocation { get; set; }
    public double MatchScale { get; init; } = 1d;
    public double TemplateMargin { get; set; }
    public double GateSpatialResidualPixels { get; set; } = double.PositiveInfinity;
    public GateDetection? AssociatedGate { get; set; }
    public int AssociatedGateIndex { get; set; } = -1;
    public SideEntranceGateAssociationKind GateAssociationKind { get; set; }
    public double StructureScore { get; set; }
    public double RawChamferPixels { get; set; } = double.PositiveInfinity;
    public double StructureCompositeCost { get; set; } = double.PositiveInfinity;
    public double StructureEdgeCoverage { get; set; }
    public double StructureOccupancyCoverage { get; set; }
    public double StructureCandidateMargin { get; set; }
    public double IdentityConfidence { get; set; }
    public SideEntranceCandidateDisposition Disposition { get; set; } =
        SideEntranceCandidateDisposition.NeedsVerification;
    public SideEntranceRejectionReason RejectionReason { get; set; }
    public string RejectionDetail { get; set; } = string.Empty;
}

public sealed class SideEntranceScanResult
{
    public GateDetectionResult GateDetection { get; init; } = new();
    public IReadOnlyList<SideEntranceScanCandidate> Candidates { get; init; } = [];
    public string FailureReason { get; init; } = string.Empty;
    public int EligibleMapCount { get; init; }
    public int ReadyMapCount { get; init; }
    public int RejectedCandidateCount { get; init; }
    public IReadOnlyList<SideEntranceScanCandidate> ReliableCandidates =>
        Candidates.Where(candidate => candidate.Disposition ==
            SideEntranceCandidateDisposition.Reliable).ToArray();
    public IReadOnlyList<SideEntranceScanCandidate> ReferenceCandidates =>
        Candidates.Where(candidate => candidate.Disposition ==
            SideEntranceCandidateDisposition.NeedsVerification).ToArray();
    public GateDetection? Gate => GateDetection.Gates
        .OrderByDescending(gate => gate.Score)
        .FirstOrDefault();
}

/// <summary>Immutable selected-map identity carried through side alignment.</summary>
public readonly record struct SideEntranceMapSelection(Guid MapId, string FloorKey)
{
    public bool IsValid => MapId != Guid.Empty && !string.IsNullOrWhiteSpace(FloorKey);
    public bool Matches(SideEntranceScanCandidate? candidate) =>
        IsValid && candidate is not null && candidate.Map.Id == MapId
        && string.Equals(candidate.FloorKey, FloorKey, StringComparison.Ordinal);
    public bool Matches(MapAlignmentSession? seed) =>
        IsValid && seed is not null && seed.MapId == MapId
        && string.Equals(seed.FloorKey, FloorKey, StringComparison.Ordinal);
    public bool Matches(Guid recognitionMapId, Guid resultMapId, string? resultFloor) =>
        IsValid && recognitionMapId == MapId && resultMapId == MapId
        && string.Equals(resultFloor, FloorKey, StringComparison.Ordinal);
    public bool Matches(
        SideEntranceScanCandidate? candidate,
        MapAlignmentSession? seed,
        Guid recognitionMapId,
        Guid resultMapId,
        string? resultFloor) =>
        Matches(candidate) && Matches(seed)
        && Matches(recognitionMapId, resultMapId, resultFloor);
}
/*
 * 文件职责：SideEntranceScanModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
