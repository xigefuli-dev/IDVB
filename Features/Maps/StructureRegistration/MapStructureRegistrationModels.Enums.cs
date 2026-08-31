namespace IDVBuff.Features.Maps;

public enum MapStructureRejectionReason
{
    None,
    InvalidInput,
    UnsupportedAlignmentMode,
    InvalidLockedScale,
    InsufficientStructure,
    QueryLargerThanReference,
    NoCandidate,
    WeakAbsoluteScore,
    AmbiguousCandidates,
    InconsistentStructure,
    ScaleChangeTooLarge,
    RefinementFailed,
    OutsideValidBounds,
    PlayerPriorMismatch,
    NativeScaleChanged,
    AnchorTransformConflict,
    TimeBudgetExceeded,
    ScaleSearchBoundary
}

public enum MapStructureEvidenceDisposition
{
    None,
    Supportive,
    Inconclusive,
    Contradictory,
    SystemError
}

public static class MapStructureRejectionReasonExtensions
{
    public static string ToDisplayText(this MapStructureRejectionReason reason) => reason switch
    {
        MapStructureRejectionReason.InvalidInput => "结构配准输入无效",
        MapStructureRejectionReason.UnsupportedAlignmentMode => "结构配准只支持等比缩放",
        MapStructureRejectionReason.InvalidLockedScale => "历史等比缩放无效，需要双门重新锁定",
        MapStructureRejectionReason.InsufficientStructure => "当前已探索地图结构过少或分布过于单一",
        MapStructureRejectionReason.QueryLargerThanReference => "当前结构范围大于参考地图，无法安全搜索",
        MapStructureRejectionReason.NoCandidate => "没有找到可用的结构候选",
        MapStructureRejectionReason.WeakAbsoluteScore => "最佳候选与墙体结构的绝对贴合度不足",
        MapStructureRejectionReason.AmbiguousCandidates => "存在多个近似房间或走廊，候选不唯一",
        MapStructureRejectionReason.InconsistentStructure => "候选只在局部区域吻合，分区证据不一致",
        MapStructureRejectionReason.ScaleChangeTooLarge => "疑似发生了超过安全范围的地图缩放",
        MapStructureRejectionReason.RefinementFailed => "局部精修未能改善结构贴合度",
        MapStructureRejectionReason.OutsideValidBounds => "候选视口超出地图有效边界",
        MapStructureRejectionReason.PlayerPriorMismatch => "候选与玩家位置先验明显冲突",
        MapStructureRejectionReason.NativeScaleChanged => "原生地图缩放与固定标定不一致",
        MapStructureRejectionReason.AnchorTransformConflict => "结构精修与锚点变换明显冲突",
        MapStructureRejectionReason.ScaleSearchBoundary => "尺度候选位于全尺度搜索边界，无法形成闭合估计",
        MapStructureRejectionReason.TimeBudgetExceeded => "结构配准超过时间预算",
        _ => string.Empty
    };

    public static MapStructureEvidenceDisposition ToDisposition(
        this MapStructureRejectionReason reason,
        bool accepted = false) =>
        accepted && reason == MapStructureRejectionReason.None
            ? MapStructureEvidenceDisposition.Supportive
            : reason switch
            {
                MapStructureRejectionReason.None =>
                    MapStructureEvidenceDisposition.None,
                MapStructureRejectionReason.InsufficientStructure
                    or MapStructureRejectionReason.QueryLargerThanReference
                    or MapStructureRejectionReason.NoCandidate
                    or MapStructureRejectionReason.WeakAbsoluteScore
                    or MapStructureRejectionReason.AmbiguousCandidates
                    or MapStructureRejectionReason.InconsistentStructure
                    or MapStructureRejectionReason.RefinementFailed
                    or MapStructureRejectionReason.ScaleSearchBoundary
                    or MapStructureRejectionReason.TimeBudgetExceeded =>
                    MapStructureEvidenceDisposition.Inconclusive,
                MapStructureRejectionReason.ScaleChangeTooLarge
                    or MapStructureRejectionReason.OutsideValidBounds
                    or MapStructureRejectionReason.PlayerPriorMismatch
                    or MapStructureRejectionReason.NativeScaleChanged
                    or MapStructureRejectionReason.AnchorTransformConflict =>
                    MapStructureEvidenceDisposition.Contradictory,
                _ => MapStructureEvidenceDisposition.SystemError
            };

    /// <summary>
    /// Classifies evidence for invalidating an already trusted alignment.
    /// Candidate-local failures (bounds, priors, ambiguity, refinement, and
    /// anchor disagreement) still reject that candidate, but do not prove the
    /// currently rendered lock is wrong. Only an explicit measured native
    /// scale change is contradictory lock evidence.
    /// </summary>
    public static MapStructureEvidenceDisposition ToContinuousLockDisposition(
        this MapStructureRejectionReason reason) => reason switch
        {
            MapStructureRejectionReason.ScaleChangeTooLarge
                or MapStructureRejectionReason.NativeScaleChanged =>
                MapStructureEvidenceDisposition.Contradictory,
            MapStructureRejectionReason.InvalidInput
                or MapStructureRejectionReason.UnsupportedAlignmentMode
                or MapStructureRejectionReason.InvalidLockedScale =>
                MapStructureEvidenceDisposition.SystemError,
            MapStructureRejectionReason.None =>
                MapStructureEvidenceDisposition.None,
            _ => MapStructureEvidenceDisposition.Inconclusive
        };
}
/*
 * 文件职责：MapStructureRegistrationModels.Enums。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
