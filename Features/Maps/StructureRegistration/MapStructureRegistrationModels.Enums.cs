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
    TimeBudgetExceeded
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
