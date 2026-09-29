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

        var confidence = attempt.StructureResult?.Confidence
            ?? attempt.Recognition?.Result.Confidence
            ?? 0d;

        var isBudgetExceeded = (attempt.FailureReason?.Contains("预算") == true)
            || (attempt.FailureReason?.Contains("budget", StringComparison.OrdinalIgnoreCase) == true);

        if (isBudgetExceeded)
        {
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.Inconclusive,
                Attempt = attempt,
                Diagnostics = attempt.Diagnostics,
                Confidence = confidence,
                FailureReason = attempt.FailureReason
            };
        }

        var isFullyAccepted = FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            attempt,
            channel,
            context.MapId,
            candidateFloorKey,
            _settings?.RecognitionTuning.MinimumConfidence ?? MapRecognitionTuning.DefaultMinimumConfidence,
            lowStructureEvidenceAccepted);

        if (isFullyAccepted)
        {
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.Accepted,
                Attempt = attempt,
                AlignedRecognition = attempt.Recognition,
                PendingRepairCacheKey = repairKey,
                Diagnostics = attempt.Diagnostics,
                Confidence = confidence
            };
        }

        if (channel == MapAlignmentChannel.LowStructure && isLowStructurePending)
        {
            return new FloorAlignmentAttemptResult
            {
                FloorKey = candidateFloorKey,
                Outcome = FloorAlignmentAttemptOutcome.PendingEvidence,
                Attempt = attempt,
                Diagnostics = attempt.Diagnostics,
                Confidence = confidence,
                LowStructurePending = true,
                FailureReason = "低结构楼层跨帧样本等待中。"
            };
        }

        var rejectionReason = attempt.StructureResult?.RejectionReason
            ?? MapStructureRejectionReason.None;

        return new FloorAlignmentAttemptResult
        {
            FloorKey = candidateFloorKey,
            Outcome = FloorAlignmentAttemptOutcome.Rejected,
            Attempt = attempt,
            RejectionReason = rejectionReason,
            FailureReason = attempt.FailureReason ?? rejectionReason.ToString(),
            Diagnostics = attempt.Diagnostics,
            Confidence = confidence
        };
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
        double totalElapsedMs)
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
        var isManualFloor = context.ManualFloorKey is not null;
        var isSingleFloorMap = orderedFloors.Count <= 1;
        // Entry geometry is authored separately for every floor. A confirmed
        // current-frame floor is not overturned because alignment lacks walls.
        var hasConfirmedEntryFloor = frame.DetectedFloorKey == initialAttemptResult.FloorKey
            && locked.Map.Floors.Any(floor => floor.Key == initialAttemptResult.FloorKey
                && floor.EntryIdentityAsset is not null);

        var shouldRecover = !hasConfirmedEntryFloor && FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(
            isManualFloor: isManualFloor,
            isSingleFloorMap: isSingleFloorMap,
            initialAttempt: initialAttemptResult);

        if (!shouldRecover)
        {
            var isInitialAccepted = initialAttemptResult.Outcome == FloorAlignmentAttemptOutcome.Accepted;
            return new FloorRecoveryDecision
            {
                Resolution = isInitialAccepted ? FloorRecoveryResolution.SingleAccepted : FloorRecoveryResolution.NotAttempted,
                Winner = isInitialAccepted ? initialAttemptResult : null,
                Attempts = [initialAttemptResult],
                Reason = isInitialAccepted ? "首选楼层初次对齐成功。" : initialAttemptResult.FailureReason
            };
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
            budgetExhausted);

        LogFloorRecoveryResolved(
            context,
            decision,
            recoveryTimer.Elapsed.TotalMilliseconds);

        return decision;
    }
}
