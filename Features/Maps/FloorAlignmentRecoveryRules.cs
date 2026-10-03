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
        FloorAlignmentAttemptResult initialAttempt,
        string? detectedFloorKey = null)
    {
        // A resolved indicator identifies the floor of this frame. Failure to
        // align it does not authorize geometry to substitute another floor.
        if (isManualFloor || isSingleFloorMap || !string.IsNullOrEmpty(detectedFloorKey))
            return false;

        // Probing and proving floor identity are separate decisions. An
        // inconclusive initial floor remains unresolved in the final verdict.
        if (initialAttempt.Outcome is not (FloorAlignmentAttemptOutcome.Rejected
            or FloorAlignmentAttemptOutcome.Inconclusive))
            return false;

        return IsStructureRejectionEligibleForRecovery(initialAttempt.RejectionReason);
    }

    public static bool IsFloorCommitAllowed(string candidateFloorKey, string? requiredFloorKey) =>
        string.IsNullOrEmpty(requiredFloorKey)
        || string.Equals(candidateFloorKey, requiredFloorKey, StringComparison.Ordinal);

    /// <summary>Preserves the structure layer's evidence semantics through floor adjudication.</summary>
    public static FloorAlignmentAttemptResult ClassifyAttemptResult(
        Guid expectedMapId,
        string candidateFloorKey,
        MapAlignmentChannel channel,
        MapRecognitionAttempt attempt,
        MapFeatureCacheKey? repairKey,
        double minimumStandardConfidence,
        bool lowStructureEvidenceAccepted,
        bool lowStructurePending)
    {
        var rejectionReason = attempt.StructureResult?.RejectionReason
            ?? MapStructureRejectionReason.None;
        var result = new FloorAlignmentAttemptResult
        {
            FloorKey = candidateFloorKey,
            Outcome = FloorAlignmentAttemptOutcome.Inconclusive,
            Attempt = attempt,
            RejectionReason = rejectionReason,
            Diagnostics = attempt.Diagnostics,
            Confidence = attempt.StructureResult?.Confidence
                ?? attempt.Recognition?.Result.Confidence ?? 0d,
            FailureReason = !string.IsNullOrWhiteSpace(attempt.FailureReason)
                ? attempt.FailureReason
                : !string.IsNullOrWhiteSpace(attempt.StructureFailureReason)
                    ? attempt.StructureFailureReason : rejectionReason.ToDisplayText()
        };

        if (rejectionReason == MapStructureRejectionReason.TimeBudgetExceeded
            || attempt.Diagnostics.StructureRejectionReason == MapStructureRejectionReason.TimeBudgetExceeded
            || attempt.FailureReason?.Contains("预算") == true
            || attempt.FailureReason?.Contains("budget", StringComparison.OrdinalIgnoreCase) == true)
        {
            return result;
        }

        if (IsAttemptFullyAccepted(attempt, channel, expectedMapId, candidateFloorKey,
                minimumStandardConfidence, lowStructureEvidenceAccepted && !lowStructurePending))
        {
            return result with
            {
                Outcome = FloorAlignmentAttemptOutcome.Accepted,
                AlignedRecognition = attempt.Recognition,
                PendingRepairCacheKey = repairKey,
                FailureReason = null
            };
        }

        if (channel == MapAlignmentChannel.LowStructure && lowStructurePending)
        {
            return result with
            {
                Outcome = FloorAlignmentAttemptOutcome.PendingEvidence,
                LowStructurePending = true,
                FailureReason = "低结构楼层跨帧样本等待中。"
            };
        }

        // Resource/configuration failures also return normally, with no
        // StructureResult. They are missing evidence, never floor rejection.
        var outcome = attempt.StructureResult is null
            ? FloorAlignmentAttemptOutcome.Error
            : rejectionReason.ToDisposition() switch
            {
                MapStructureEvidenceDisposition.Contradictory => FloorAlignmentAttemptOutcome.Rejected,
                MapStructureEvidenceDisposition.SystemError => FloorAlignmentAttemptOutcome.Error,
                _ => FloorAlignmentAttemptOutcome.Inconclusive
            };
        return result with { Outcome = outcome };
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
        bool budgetExhausted,
        string? requiredFloorKey = null)
    {
        if (attempts.Count == 0)
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.Inconclusive,
                Attempts = attempts,
                Reason = "没有可用的楼层对齐证据。"
            };
        }

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
        if (accepted.Any(attempt => !IsFloorCommitAllowed(attempt.FloorKey, requiredFloorKey)))
        {
            return new FloorRecoveryDecision
            {
                Resolution = FloorRecoveryResolution.Inconclusive,
                Attempts = attempts,
                Reason = $"对齐候选与本帧指示器或手动指定楼层 {requiredFloorKey} 冲突，暂不提交。"
            };
        }
        var pending = attempts.Where(attempt => attempt.Outcome == FloorAlignmentAttemptOutcome.PendingEvidence).ToList();
        // A failed calculation is missing evidence, not evidence against that
        // floor. It cannot make another candidate the unique winner.
        var inconclusive = attempts.Where(attempt => attempt.Outcome is
            FloorAlignmentAttemptOutcome.Inconclusive or FloorAlignmentAttemptOutcome.Error
            || (attempt.Outcome == FloorAlignmentAttemptOutcome.Rejected
                && attempt.RejectionReason.ToDisposition() != MapStructureEvidenceDisposition.Contradictory)).ToList();

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
                    Reason = "已有一层通过结构初验，但其他楼层证据不足或试探未完成，不能断言唯一胜者。"
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

internal static partial class LowStructureScaleEvidenceRules
{
    public static AlignmentEvidence ObserveAlignment(MapRecognitionAttempt attempt)
    {
        var accepted = attempt.StructureAccepted
            && attempt.StructureResult is { Accepted: true, WasForcedBestCandidate: false }
            && attempt.Recognition is { Result.ReusedLastTransform: false } recognition
            && recognition.Result.OverlayTransform is { } transform
            && double.IsFinite(transform.ScaleX) && transform.ScaleX > 0d
            && double.IsFinite(transform.ScaleY) && transform.ScaleY > 0d
            && double.IsFinite(transform.OffsetX) && double.IsFinite(transform.OffsetY);
        var independentlyEstimated = accepted
            && IsIndependentScaleRoute(attempt.Diagnostics.LowStructureRoute);
        // Fixed-scale geometry can validate a new translation once its exact
        // floor/capture seed is reliable. It must never vote for scale trust.
        var validatedScaleSeed = attempt.Diagnostics.WarmStateHit
            || attempt.Diagnostics.LowStructureValidatedScaleSeed;
        return new(
            accepted,
            independentlyEstimated ? 1 : 0,
            accepted && !independentlyEstimated && !validatedScaleSeed);
    }

    internal readonly record struct AlignmentEvidence(bool Accepted, int Count, bool Pending);
}
