// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 对局会话状态枚举。对应旧代码中 MapSessionState。
/// </summary>
public enum SessionPhase
{
    Closed = 0,
    OpeningDetected = 1,
    WaitingForStableFrames = 2,
    IdentifyingMap = 3,
    CoarseLocating = 4,
    FineLocating = 5,
    Confirming = 6,
    Locked = 7,
    LowConfidence = 8,
    Lost = 9,
    RecalibrationRequired = 10
}
