using IDVBuff.Core.Diagnostics;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void SetConfirmedFloorPreference(Guid mapId, string floorKey)
    {
        _recentConfirmedFloorMapId = mapId;
        _recentConfirmedFloorPreference = floorKey;
    }

    private string? GetConfirmedFloorPreference(Guid mapId)
    {
        return _recentConfirmedFloorMapId == mapId ? _recentConfirmedFloorPreference : null;
    }

    private async Task<FloorAlignmentAttemptResult> EvaluateFloorHypothesisAsync(
        MapOpenOperationContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string candidateFloorKey,
        CancellationToken cancellationToken)
    {
        if (!IsMapOpenOperationCurrent(context))
        {
            var dummyDiag = MapCvRecognitionDiagnostics.CreateDiagnostics(0, 0);
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.Superseded,
                Attempt = MapCvRecognitionDiagnostics.Failure(dummyDiag, "操作已被取代。"),
                Diagnostics = dummyDiag,
                FailureReason = "操作已被取代。"
            };
        }

        var channelDef = MapAlignmentChannelRegistry.Resolve(locked.Map, candidateFloorKey);
        var channel = channelDef.Channel;
        var alignmentMode = _settings!.OverlayAlignmentMode;
        var structureTuning = CreateStructureTuningForFloor(
            locked.Map,
            candidateFloorKey,
            CreateEffectiveStructureTuning());
        var tuning = _settings.RecognitionTuning.Clone();
        if (tuning.GateTemplateThreshold > GateTemplateRules.FallbackPairThreshold)
            tuning.GateTemplateThreshold = GateTemplateRules.FallbackPairThreshold;

        var scaleSeed = MapFloorScaleSeedRules.CreateIndependentFloorSeed(locked.Map, candidateFloorKey);

        MapRecognitionAttempt attempt;
        MapFeatureCacheKey? repairKey = null;

        try
        {
            (attempt, repairKey) = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var alignmentDeadline = new NoDoorAlignmentDeadline(
                    cancellationToken,
                    MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds,
                    enforceTimeBudget: false);
                using var alignmentBudget = alignmentDeadline.EnterAmbient();

                var att = AlignExactManualFloor(
                    frame,
                    locked,
                    candidateFloorKey,
                    scaleSeed,
                    alignmentMode,
                    tuning,
                    structureTuning,
                    identityPriorConfidence: 1.0,
                    out var repKey,
                    isHypothesis: true);

                return (att, repKey);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorDiag = MapCvRecognitionDiagnostics.CreateDiagnostics(0, 0);
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.Error,
                Attempt = MapCvRecognitionDiagnostics.Failure(errorDiag, ex.Message),
                Diagnostics = errorDiag,
                FailureReason = ex.Message
            };
        }

        if (!IsMapOpenOperationCurrent(context))
        {
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.Superseded,
                Attempt = attempt,
                Diagnostics = attempt.Diagnostics,
                FailureReason = "操作已被取代。"
            };
        }

        return ClassifyAttemptResult(context, candidateFloorKey, channel, attempt, repairKey);
    }

    private FloorAlignmentAttemptResult ClassifyAttemptResult(
        MapOpenOperationContext context,
        string candidateFloorKey,
        MapAlignmentChannel channel,
        MapRecognitionAttempt attempt,
        MapFeatureCacheKey? repairKey)
    {
        var lowStructureEvidenceAccepted = true;
        var isLowStructurePending = false;
        if (channel == MapAlignmentChannel.LowStructure)
        {
            var evidence = ObserveLowStructureEvidence(attempt);
            lowStructureEvidenceAccepted = evidence.Accepted && !evidence.Pending;
            isLowStructurePending = evidence.Pending;
            attempt.Diagnostics.LowStructureEvidenceCount = evidence.Count;
            attempt.Diagnostics.LowStructureEvidencePending = evidence.Pending;
        }

        return FloorAlignmentRecoveryRules.ClassifyAttemptResult(
            context.MapId,
            candidateFloorKey,
            channel,
            attempt,
            repairKey,
            _settings?.RecognitionTuning.MinimumConfidence ?? MapRecognitionTuning.DefaultMinimumConfidence,
            lowStructureEvidenceAccepted,
            isLowStructurePending);
    }

    private void LogFloorProposal(
        MapOpenOperationContext context,
        string proposedFloorKey,
        double? score,
        double? margin)
    {
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Info,
            $"FloorProposal · map={context.MapId} · floor={proposedFloorKey}",
            details: new()
            {
                ["event"] = "FloorProposal",
                ["matchId"] = context.MatchId,
                ["mapId"] = context.MapId,
                ["mapUpdatedAt"] = context.MapUpdatedAt,
                ["generation"] = context.OperationGeneration,
                ["toggleVersion"] = context.MapToggleVersion,
                ["frameId"] = context.CaptureFrameId,
                ["previousConfirmedFloor"] = GetConfirmedFloorPreference(context.MapId),
                ["currentFloor"] = _currentFloorKey,
                ["proposedFloor"] = proposedFloorKey,
                ["score"] = score,
                ["margin"] = margin
            });
    }

    private void LogFloorRecoveryStarted(
        MapOpenOperationContext context,
        string initialFloorKey,
        MapStructureRejectionReason triggerReason,
        IReadOnlyList<string> alternativeFloors)
    {
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Info,
            $"FloorRecoveryStarted · map={context.MapId} · initialFloor={initialFloorKey} · reason={triggerReason} · alternatives=[{string.Join(",", alternativeFloors)}]",
            details: new()
            {
                ["event"] = "FloorRecoveryStarted",
                ["matchId"] = context.MatchId,
                ["sourceMapId"] = context.MapId,
                ["currentMapId"] = _lastRecognition?.Map.Id,
                ["generation"] = context.OperationGeneration,
                ["initialFloor"] = initialFloorKey,
                ["triggerReason"] = triggerReason.ToString(),
                ["alternativeFloors"] = alternativeFloors
            });
    }

    private void LogFloorRecoveryCandidateCompleted(
        MapOpenOperationContext context,
        FloorAlignmentAttemptResult candidate,
        double elapsedMs)
    {
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Info,
            $"FloorRecoveryCandidateCompleted · floor={candidate.FloorKey} · outcome={candidate.Outcome}",
            elapsedMs: elapsedMs,
            details: new()
            {
                ["event"] = "FloorRecoveryCandidateCompleted",
                ["matchId"] = context.MatchId,
                ["sourceMapId"] = context.MapId,
                ["currentMapId"] = _lastRecognition?.Map.Id,
                ["floor"] = candidate.FloorKey,
                ["outcome"] = candidate.Outcome.ToString(),
                ["rejectionReason"] = candidate.RejectionReason.ToString(),
                ["confidence"] = candidate.Confidence,
                ["hasTransform"] = candidate.AlignedRecognition?.Result.OverlayTransform is not null,
                ["reusedLastTransform"] = candidate.AlignedRecognition?.Result.ReusedLastTransform ?? false
            });
    }

    private void LogFloorRecoveryResolved(
        MapOpenOperationContext context,
        FloorRecoveryDecision decision,
        double totalElapsedMs,
        string? requiredFloorKey)
    {
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            decision.Resolution == FloorRecoveryResolution.SingleAccepted ? MapLogLevel.Info : MapLogLevel.Warning,
            $"FloorRecoveryResolved · resolution={decision.Resolution} · winner={decision.Winner?.FloorKey ?? "none"}",
            elapsedMs: totalElapsedMs,
            details: new()
            {
                ["event"] = "FloorRecoveryResolved",
                ["resolution"] = decision.Resolution.ToString(),
                ["winnerFloor"] = decision.Winner?.FloorKey,
                ["requiredFloor"] = requiredFloorKey,
                ["previousConfirmedFloor"] = GetConfirmedFloorPreference(context.MapId),
                ["currentFloor"] = _currentFloorKey,
                ["frameId"] = context.CaptureFrameId,
                ["generation"] = context.OperationGeneration,
                ["initialAttemptFloor"] = decision.Attempts.FirstOrDefault()?.FloorKey,
                ["initialAttemptOutcome"] = decision.Attempts.FirstOrDefault()?.Outcome.ToString(),
                ["lowStructureRoute"] = decision.Attempts.FirstOrDefault()?.Diagnostics.LowStructureRoute,
                ["lowStructureEvidencePending"] = decision.Attempts.FirstOrDefault()?.Diagnostics.LowStructureEvidencePending,
                ["lowStructureValidatedScaleSeed"] = decision.Attempts.FirstOrDefault()?.Diagnostics.LowStructureValidatedScaleSeed,
                ["warmStateHit"] = decision.Attempts.FirstOrDefault()?.Diagnostics.WarmStateHit,
                ["reason"] = decision.Reason,
                ["attemptsCount"] = decision.Attempts.Count,
                ["matchId"] = context.MatchId,
                ["sourceMapId"] = context.MapId,
                ["currentMapId"] = _lastRecognition?.Map.Id
            });
    }
    private async Task<FloorRecoveryDecision> ExecuteFloorRecoveryWorkflowAsync(
        MapOpenOperationContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        FloorAlignmentAttemptResult initialAttemptResult,
        IReadOnlyList<string> orderedFloors,
        string? preferredConfirmed,
        CancellationToken cancellationToken)
    {
        var isManualFloor = context.IsManualFloor;
        var isSingleFloorMap = orderedFloors.Count <= 1;
        var requiredFloorKey = context.ManualFloorKey ?? frame.DetectedFloorKey;

        var shouldRecover = FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(
            isManualFloor: isManualFloor,
            isSingleFloorMap: isSingleFloorMap,
            initialAttempt: initialAttemptResult,
            detectedFloorKey: frame.DetectedFloorKey);

        if (!shouldRecover)
        {
            var initialDecision = FloorAlignmentRecoveryRules.DecideFromAttempts(
                [initialAttemptResult], budgetExhausted: false, requiredFloorKey);
            if (initialDecision.Resolution == FloorRecoveryResolution.SingleAccepted)
            {
                initialDecision = initialDecision with
                {
                    Reason = $"目标楼层 {initialAttemptResult.FloorKey} 初次对齐成功。"
                };
            }
            if (initialDecision.Resolution == FloorRecoveryResolution.AllRejected)
            {
                initialDecision = initialDecision with
                {
                    Resolution = FloorRecoveryResolution.NotAttempted,
                    Reason = initialAttemptResult.FailureReason
                };
            }
            if (initialDecision.Resolution != FloorRecoveryResolution.SingleAccepted
                && !string.IsNullOrEmpty(requiredFloorKey))
            {
                initialDecision = initialDecision with
                {
                    Reason = $"本次楼层依据为 {requiredFloorKey}，该层暂未完成对齐："
                        + (initialAttemptResult.FailureReason ?? initialDecision.Reason)
                };
            }
            LogFloorRecoveryResolved(context, initialDecision, 0d, requiredFloorKey);
            return initialDecision;
        }

        var altFloors = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(
            locked.Map,
            initialAttemptResult.FloorKey,
            preferredConfirmed);

        LogFloorRecoveryStarted(
            context,
            initialAttemptResult.FloorKey,
            initialAttemptResult.RejectionReason,
            altFloors);

        var recoveryAttempts = new List<FloorAlignmentAttemptResult> { initialAttemptResult };
        var recoveryTimer = Stopwatch.StartNew();
        var budgetExhausted = false;

        foreach (var altFloor in altFloors)
        {
            if (!IsMapOpenOperationCurrent(context))
            {
                recoveryAttempts.Add(new FloorAlignmentAttemptResult
                {
                    FloorKey = altFloor,
                    Outcome = FloorAlignmentAttemptOutcome.Superseded,
                    Attempt = initialAttemptResult.Attempt,
                    Diagnostics = initialAttemptResult.Diagnostics,
                    FailureReason = "操作已被取代。"
                });
                break;
            }

            if (recoveryTimer.ElapsedMilliseconds >= MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds)
            {
                budgetExhausted = true;
                break;
            }

            var candidateTimer = Stopwatch.StartNew();
            var candidateResult = await EvaluateFloorHypothesisAsync(
                context,
                frame,
                locked,
                altFloor,
                cancellationToken);

            LogFloorRecoveryCandidateCompleted(
                context,
                candidateResult,
                candidateTimer.Elapsed.TotalMilliseconds);

            recoveryAttempts.Add(candidateResult);
        }

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            recoveryAttempts,
            budgetExhausted,
            requiredFloorKey);

        LogFloorRecoveryResolved(
            context,
            decision,
            recoveryTimer.Elapsed.TotalMilliseconds,
            requiredFloorKey);

        return decision;
    }

    private bool TryValidateFloorCommit(
        RuntimeMapRecognition aligned,
        string targetFloorKey,
        string? requiredFloorKey,
        MapOpenOperationContext? context,
        out string? failureReason)
    {
        failureReason = null;
        if (string.Equals(aligned.Result.Floor, targetFloorKey, StringComparison.Ordinal)
            && FloorAlignmentRecoveryRules.IsFloorCommitAllowed(aligned.Result.Floor, requiredFloorKey))
            return true;

        failureReason = $"对齐结果楼层 {aligned.Result.Floor} 与本次楼层依据 "
            + $"{requiredFloorKey ?? targetFloorKey} 冲突，等待重新开图确认。";
        _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Warning,
            "FloorCommitRejected · reason=floor-evidence-conflict", details: new()
            {
                ["event"] = "FloorCommitRejected",
                ["mapId"] = aligned.Map.Id,
                ["candidateFloor"] = aligned.Result.Floor,
                ["requiredFloor"] = requiredFloorKey,
                ["targetFloor"] = targetFloorKey,
                ["currentFloor"] = _currentFloorKey,
                ["previousConfirmedFloor"] = GetConfirmedFloorPreference(aligned.Map.Id),
                ["frameId"] = context?.CaptureFrameId
            });
        return false;
    }

    private void LogFloorCommitted(
        RuntimeMapRecognition aligned,
        string? requiredFloorKey,
        string? previousFloorKey,
        string? previousConfirmedFloor,
        MapOpenOperationContext? context)
    {
        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
            $"仅对齐完成 · map={aligned.Map.Id} · floor={aligned.Result.Floor}",
            details: new()
            {
                ["mapId"] = aligned.Map.Id,
                ["floor"] = aligned.Result.Floor,
                ["committedFloor"] = aligned.Result.Floor,
                ["requiredFloor"] = requiredFloorKey,
                ["previousFloor"] = previousFloorKey,
                ["previousConfirmedFloor"] = previousConfirmedFloor,
                ["frameId"] = context?.CaptureFrameId,
                ["commitReason"] = requiredFloorKey is null ? "target-floor-alignment" : "floor-evidence-and-alignment",
                ["identityConfidence"] = aligned.Result.IdentityConfidence,
                ["localizationConfidence"] = aligned.Result.LocalizationConfidence,
                ["candidateMargin"] = MapFeatureCacheRules.GetCandidateMargin(aligned.Result)
            });
    }
}
