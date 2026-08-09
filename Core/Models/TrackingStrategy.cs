// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 对齐追踪策略枚举。对应旧代码中 MapAlignmentTrackingMode。
/// </summary>
public enum TrackingStrategy
{
    None = 0,
    NeedsGatePair = 1,
    GatePairLocked = 2,
    SingleGateTracking = 3,
    AuxiliaryAnchorTracking = 4,
    WaitingForAnchor = 5,
    StructureMatched = 6,
    HoldingLastTransform = 7,
    Lost = 8
}
