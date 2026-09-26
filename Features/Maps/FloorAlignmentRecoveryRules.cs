using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 纯规则类：负责跨楼层恢复的触发判定、候选楼层解析、通道完整验收与最终胜者裁决。
/// 无任何外部状态依赖，便于 100% 独立单元测试。
/// </summary>
public static class FloorAlignmentRecoveryRules
{
    /// <summary>
    /// 触发跨楼层恢复的结构拒绝白名单原因。
    /// </summary>
    public static bool IsStructureRejectionEligibleForRecovery(MapStructureRejectionReason reason) =>
        reason is MapStructureRejectionReason.WeakAbsoluteScore
            or MapStructureRejectionReason.NoCandidate
            or MapStructureRejectionReason.InconsistentStructure
            or MapStructureRejectionReason.QueryLargerThanReference
            or MapStructureRejectionReason.LowGeometricLockConfidence;

    /// <summary>
    /// 判断首选楼层初次对齐结果是否应该触发跨楼层恢复。
    /// </summary>
    public static bool ShouldAttemptFloorRecovery(
        bool isManualFloor,
        bool isSingleFloorMap,
        FloorAlignmentAttemptResult initialAttempt)
    {
        // 手动明确指定楼层或单楼层地图，绝不触发跨楼层恢复
        if (isManualFloor || isSingleFloorMap)
            return false;

        // 仅在明确的结构性拒绝下进入恢复，捕获失败、预算耗尽、已成功等均不触发
        if (initialAttempt.Outcome != FloorAlignmentAttemptOutcome.Rejected)
            return false;

        return IsStructureRejectionEligibleForRecovery(initialAttempt.RejectionReason);
    }

    /// <summary>
    /// 解析同一地图内待试探的其他楼层顺序。
    /// 排除当前首选楼层，若有最近成功确认的短期偏好楼层则优先尝试，其余按地图定义顺序排列。
    /// </summary>
    public static IReadOnlyList<string> ResolveAlternativeFloors(
        MapRecord map,
        string initialFloorKey,
        string? recentConfirmedFloorKey)
    {
        var allFloors = MapFloorRules.GetOrderedFloors(map)
            .Select(floor => floor.Key)
            .Where(floorKey => !string.Equals(floorKey, initialFloorKey, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (allFloors.Count <= 1)
            return allFloors;

        if (!string.IsNullOrEmpty(recentConfirmedFloorKey)
            && allFloors.Contains(recentConfirmedFloorKey, StringComparer.Ordinal))
        {
            allFloors.Remove(recentConfirmedFloorKey);
            allFloors.Insert(0, recentConfirmedFloorKey);
        }

        return allFloors;
    }

    /// <summary>
    /// 验证候选楼层的对齐结果是否完整通过该通道的所有结构验收标准。
    /// </summary>
    public static bool IsAttemptFullyAccepted(
        MapRecognitionAttempt attempt,
        MapAlignmentChannel channel,
        Guid expectedMapId,
        string candidateFloorKey,
        double minimumStandardConfidence,
        bool lowStructureEvidenceAccepted)
    {
        if (attempt.Recognition is null)
            return false;

        var rec = attempt.Recognition;
        if (rec.Map.Id != expectedMapId)
            return false;

        if (!string.Equals(rec.Result.Floor, candidateFloorKey, StringComparison.Ordinal))
            return false;

        if (rec.Result.OverlayTransform is not { } transform)
            return false;

        if (!double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0d
            || !double.IsFinite(transform.ScaleY) || transform.ScaleY <= 0d
            || !double.IsFinite(transform.OffsetX)
            || !double.IsFinite(transform.OffsetY))
        {
            return false;
        }

        if (rec.Result.ReusedLastTransform)
            return false;

        if (attempt.StructureResult is { WasForcedBestCandidate: true })
            return false;

        var confidence = attempt.StructureResult?.Confidence ?? rec.Result.Confidence;
        if (!MapOpenAlignmentRouteRules.IsAcceptedStructureAlignment(
                channel,
                attempt.StructureAccepted,
                hasTransform: true,
                confidence,
                minimumStandardConfidence))
        {
            return false;
        }

        if (channel == MapAlignmentChannel.LowStructure && !lowStructureEvidenceAccepted)
            return false;

        return true;
    }

    /// <summary>
    /// 根据所有候选楼层的试探结果以及预算执行状态，作出最终恢复决策。
    /// 必须且仅当唯一候选通过完整验收且无未决竞争时才返回单一胜者；多个通过返回歧义；有未决返回等待/超时。
    /// </summary>
    public static FloorRecoveryDecision DecideFromAttempts(
        IReadOnlyList<FloorAlignmentAttemptResult> attempts,
        bool budgetExhausted)
    {
        if (attempts.Any(attempt => attempt.Outcome == FloorAlignmentAttemptOutcome.Superseded))
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.Superseded,
                Attempts = attempts,
                Reason = "操作已被新操作取代或已失效。"
            };
        }

        var accepted = attempts.Where(attempt => attempt.Outcome == FloorAlignmentAttemptOutcome.Accepted).ToList();
        var pending = attempts.Where(attempt => attempt.Outcome == FloorAlignmentAttemptOutcome.PendingEvidence).ToList();
        // A failed calculation is missing evidence, not evidence against that
        // floor. It cannot make another candidate the unique winner.
        var inconclusive = attempts.Where(attempt => attempt.Outcome is
            FloorAlignmentAttemptOutcome.Inconclusive or FloorAlignmentAttemptOutcome.Error).ToList();

        if (accepted.Count == 1)
        {
            // 如果存在其他候选处于等待跨帧证据或未完成试探状态，不能伪造唯一性
            if (pending.Count > 0)
            {
                return new FloorRecoveryDecision
                {
                    Resolution = FloorRecoveryResolution.PendingEvidence,
                    Attempts = attempts,
                    Reason = "已有一层通过结构初验，但另一层仍处于跨帧证据等待中，暂不宣布唯一胜者。"
                };
            }

            if (inconclusive.Count > 0 || budgetExhausted)
            {
                return new FloorRecoveryDecision
                {
                    Resolution = FloorRecoveryResolution.Inconclusive,
                    Attempts = attempts,
                    Reason = "已有一层通过结构初验，但部分楼层因预算或资源未完成试探，不能断言唯一胜者。"
                };
            }

            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.SingleAccepted,
                Winner = accepted[0],
                Attempts = attempts,
                Reason = $"唯一候选楼层 {accepted[0].FloorKey} 完整通过结构验证。"
            };
        }

        if (accepted.Count > 1)
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.Ambiguous,
                Attempts = attempts,
                Reason = $"存在多个楼层（{string.Join(", ", accepted.Select(a => a.FloorKey))}）同时通过结构验证，属于楼层歧义。"
            };
        }

        // accepted.Count == 0
        if (pending.Count > 0)
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.PendingEvidence,
                Attempts = attempts,
                Reason = "所有尝试中存在楼层处于低结构跨帧证据等待中。"
            };
        }

        if (inconclusive.Count > 0 || budgetExhausted)
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.Inconclusive,
                Attempts = attempts,
                Reason = "楼层恢复试探未全部完成或超出预算。"
            };
        }

        return new FloorRecoveryDecision
        {
            Resolution = FloorRecoveryResolution.AllRejected,
            Attempts = attempts,
            Reason = "当前地图所有候选楼层均明确未通过结构验证。"
        };
    }
}
