using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private List<(SideEntranceScanCandidate Candidate, MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>
        VerifySideEntranceCandidates(CapturedGameFrame frame, IReadOnlyList<SideEntranceScanCandidate> candidates,
            MapRecognitionTuning sideAlignmentTuning, Dictionary<string, double> timings,
            ScanIdentitySelectionPolicy selectionPolicy)
    {
        var context = ScanExecutionContext.Current;
        var evidenceFrame = context?.Frame;
        var reliable = new List<(SideEntranceScanCandidate Candidate, MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>();
        if (evidenceFrame is null) return reliable;
        var timer = Stopwatch.StartNew();
        // Compare the whole class before spending time on formal alignment.
        // A cold registration of one family must not starve unrelated identities.
        foreach (var candidate in candidates)
        {
            if (!context!.CanCompute) break;
            var hypotheses = candidate.SearchHypotheses.Count > 0 ? candidate.SearchHypotheses : new[] { candidate };
            foreach (var hypothesis in hypotheses)
            {
                if (!context.CanCompute) break;
                if (hypothesis.StructureIndex is { } index
                    && _recognition.TryCreateSideEntranceAlignmentSeed(hypothesis, frame.ViewportBounds, out var seed, out _))
                    hypothesis.IdentityEvidence = ScanIdentityVerifier.Verify(evidenceFrame, index,
                        seed.LockedTransform, frame.ViewportBounds, context);
            }
            candidate.IdentityEvidence = hypotheses.Select(h => h.IdentityEvidence)
                .OrderByDescending(e => e.State == ScanIdentityState.Supported)
                .ThenByDescending(e => e.State == ScanIdentityState.Unverified)
                .ThenBy(ScanIdentityVerifier.FitCost).First();
            candidate.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        }
        timings["scan_class_evidence"] = timer.Elapsed.TotalMilliseconds;
        var deferAmbiguousAlignment = ScanUncertainPolicies.DeferAmbiguousDeepScanAlignment(context!, candidates);
        var completed = 0;
        var formal = 0;
        var reusedFamily = 0;
        var dominatedAlignments = 0;
        using var budget = MapNoDoorAlignmentBudgetContext.Enter(() => Math.Max(0, context!.RemainingMilliseconds - 60));
        foreach (var candidate in candidates.OrderByDescending(c => c.IdentityEvidence.State == ScanIdentityState.Supported)
            .ThenBy(c => ScanIdentityVerifier.FitCost(c.IdentityEvidence))
            .ThenBy(c => c.Map.SequenceNumber).ThenBy(c => c.Map.Id))
        {
            if (!context!.CanCompute) break;
            if (context.VariantGroups.Any(group => group.Contains(candidate.Map.Id)
                && reliable.Any(item => group.Contains(item.Candidate.Map.Id))))
            {
                // Its own floor/anchor evidence was compared above. Reusing the
                // family decision never copies a sibling's pose or marks it green.
                reusedFamily++;
                continue;
            }
            var hypotheses = candidate.SearchHypotheses.Count > 0 ? candidate.SearchHypotheses : new[] { candidate };
            var complete = true;
            var supported = false;
            ScanIdentityEvidence? bestEvidence = null;
            MapAlignmentSession? bestSeed = null;
            MapRecognitionAttempt? bestAttempt = null;
            foreach (var proposal in deferAmbiguousAlignment ? Array.Empty<SideEntranceScanCandidate>() : hypotheses)
            {
                var hypothesis = proposal;
                if (!context.CanCompute) { complete = false; break; }
                if (hypothesis.StructureIndex is not { } index
                    || !_recognition.TryCreateSideEntranceAlignmentSeed(hypothesis, frame.ViewportBounds, out var seed, out _))
                { complete = false; continue; }
                var evidence = hypothesis.IdentityEvidence;
                var competitiveCost = selectionPolicy == ScanIdentitySelectionPolicy.AllowDominantSupport
                    && reliable.Count > 0
                    ? reliable.Min(item => ScanIdentityVerifier.FitCost(item.Candidate.IdentityEvidence))
                        + ScanIdentityVerifier.DominantFitMargin
                    : double.PositiveInfinity;
                if (evidence.State == ScanIdentityState.Excluded && evidence.SupportedFraction >= .80
                    && SideEntranceScanPipeline.RefineIdentityPose(hypothesis, evidenceFrame, frame.ViewportBounds,
                        context, competitiveCost) is { } refined
                    && _recognition.TryCreateSideEntranceAlignmentSeed(refined, frame.ViewportBounds, out var refinedSeed, out _))
                {
                    hypothesis = refined;
                    seed = refinedSeed;
                    evidence = refined.IdentityEvidence;
                    candidate.SearchHypotheses = hypotheses.Append(refined).ToArray();
                }
                // A weaker alternative pose must not erase this identity's best
                // support before the family-versus-outsider comparison below.
                if (evidence.State == ScanIdentityState.Supported
                    && (candidate.IdentityEvidence.State != ScanIdentityState.Supported
                        || ScanIdentityVerifier.FitCost(evidence) < ScanIdentityVerifier.FitCost(candidate.IdentityEvidence)))
                    candidate.IdentityEvidence = evidence;
                if (evidence.State == ScanIdentityState.Unverified) { complete = false; break; }
                if (evidence.State != ScanIdentityState.Supported) continue;
                // Refinement can recover a weak outside identity. Keep that
                // evidence in the final comparison, but do not spend a full
                // registration/rescue on it if a verified family already wins.
                // Later refinements and the commit guard rerun the same decision.
                if (ScanIdentityVerifier.SelectIdentity(candidates, context.RetrievalCompleted,
                    context.CanCompute, context.VariantGroups, selectionPolicy) is { } winner
                    && winner != candidate.Map.Id)
                {
                    dominatedAlignments++;
                    continue;
                }
                LogScanVerificationCandidateSelected(hypothesis, completed);
                var structureTuning = CreateScanVerificationTuning(
                    MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                        CreateStructureTuningForFloor(candidate.Map, candidate.FloorKey, CreateInitialAlignmentStructureTuning())));
                structureTuning.StructureFallbackBudgetMilliseconds = Math.Max(1, Math.Min(
                    structureTuning.StructureFallbackBudgetMilliseconds, context.RemainingMilliseconds - 60));
                var attempt = context.Policy.Mode == ScanPerformanceMode.DeepScan
                    ? _recognition.AlignDeepScan(frame, hypothesis, seed, sideAlignmentTuning, structureTuning)
                    : RunMandatoryCandidateStructureRegistration(frame, hypothesis, seed,
                        sideAlignmentTuning, structureTuning, out seed);
                formal++;
                if (attempt.Recognition?.Result.OverlayTransform is not { } finalTransform || !attempt.StructureAccepted)
                {
                    // The original pose still supports this identity. It cannot be
                    // selected without alignment, but it must remain a competitor.
                    complete = false;
                    continue;
                }
                // Re-evaluate the transform that will actually be consumed, including rescue/precision changes.
                evidence = ScanIdentityVerifier.Verify(evidenceFrame, index, finalTransform, frame.ViewportBounds, context);
                if (evidence.State == ScanIdentityState.Unverified) { complete = false; break; }
                // Rejection of the moved transform does not disprove the original
                // supported identity; registration has not confirmed a usable pose.
                if (evidence.State != ScanIdentityState.Supported) { complete = false; continue; }
                hypothesis.VerifiedTransform = finalTransform;
                hypothesis.IdentityEvidence = evidence;
                if (bestEvidence is null || evidence.ForwardMeanPixels < bestEvidence.ForwardMeanPixels)
                {
                    bestEvidence = evidence;
                    bestSeed = seed;
                    bestAttempt = attempt;
                }
                supported = true;
                // One confirmed pose establishes this identity's supported fit.
                // Spend the remaining shared budget on competing identities, not
                // repeated registrations of this already usable map.
                break;
            }
            // Alternative poses belong to the same identity. A failed alternative
            // cannot revoke a pose that passed both formal registration and final
            // evidence verification. Other identities and the shared deadline are
            // still checked independently by SelectIdentity and the commit guard.
            if (supported && bestEvidence is not null)
            {
                SideEntranceCandidateEvidence.ApplyStructureAttempt(candidate, bestAttempt!);
                candidate.IdentityEvidence = bestEvidence;
                candidate.VerifiedTransform = bestAttempt!.Recognition!.Result.OverlayTransform;
                candidate.RawChamferPixels = bestEvidence.ForwardMeanPixels;
                candidate.IdentityConfidence = bestEvidence.SupportedFraction;
                candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
                candidate.RejectionReason = SideEntranceRejectionReason.None;
                candidate.RejectionDetail = string.Empty;
                reliable.Add((candidate, bestSeed!, bestAttempt!));
            }
            else
            {
                candidate.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
                candidate.RejectionDetail = complete ? candidate.IdentityEvidence.Reason : "alignment-not-confirmed";
            }
            completed++;
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                $"扫描身份复核 · map={candidate.Map.SequenceNumber}#{candidate.FloorKey} · {candidate.IdentityEvidence.State}",
                details: new()
                {
                    ["mapId"] = candidate.Map.Id, ["mode"] = context.Policy.Mode.ToString(),
                    ["testedPoints"] = candidate.IdentityEvidence.TestedPoints,
                    ["totalPoints"] = candidate.IdentityEvidence.TotalPoints,
                    ["support"] = candidate.IdentityEvidence.SupportedFraction,
                    ["forwardMeanPixels"] = double.IsFinite(candidate.IdentityEvidence.ForwardMeanPixels) ? candidate.IdentityEvidence.ForwardMeanPixels : null,
                    ["longestConflictPixels"] = candidate.IdentityEvidence.LongestConflictPixels,
                    ["evidence"] = candidate.IdentityEvidence,
                    ["supportTolerancePixels"] = ScanIdentityVerifier.SupportTolerancePixels,
                    ["continuousConflictLimitPixels"] = ScanIdentityVerifier.MaximumContinuousConflictPixels,
                    ["coordinateSpace"] = "viewport-pixels",
                    ["hypothesisCount"] = candidate.SearchHypotheses.Count,
                    ["hypotheses"] = candidate.SearchHypotheses
                        .Where(h => h.IdentityEvidence.SupportedFraction >= .80).Take(8).Select(h => new
                    {
                        h.MatchScale, h.MatchLocation, h.IdentityEvidence, h.VerifiedTransform
                    }).ToArray(),
                    ["reason"] = candidate.IdentityEvidence.Reason
                });
            _scanProgressOverlay.Report(.76 + .12 * completed / candidates.Count, "正在比较地图结构...");
        }
        timings["scan_verification"] = timer.Elapsed.TotalMilliseconds;
        _lastDiagnostics!.ScanCandidateCount = candidates.Count;
        _lastDiagnostics.ScanVerificationCandidateCount = candidates.Count;
        context!.VerifiedCandidateCount = candidates.Count(c => c.IdentityEvidence.State != ScanIdentityState.Unverified);
        _lastDiagnostics.ScanVerifiedCandidateCount = context.VerifiedCandidateCount.Value;
        _lastDiagnostics.ScanVerificationTimedOut = !context!.CanCompute;
        _lastDiagnostics.ScanEarlyExited = false;
        _lastDiagnostics.ScanFormalStructureAttemptCount = formal;
        _lastDiagnostics.ScanFormalStructureAcceptedCount = reliable.Count;
        _lastDiagnostics.ScanTotalVerificationMilliseconds = timer.Elapsed.TotalMilliseconds;
        _lastDiagnostics.ScanEffectiveBudgetMilliseconds = context.Policy.BudgetMilliseconds;
        _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            "扫描分阶段验证完成",
            details: new()
            {
                ["comparedIdentities"] = _lastDiagnostics.ScanVerifiedCandidateCount,
                ["formalAlignments"] = formal,
                ["verifiedMembers"] = reliable.Count,
                ["skippedSiblingAlignments"] = reusedFamily,
                ["skippedDominatedAlignments"] = dominatedAlignments,
                ["deferredAmbiguousAlignment"] = deferAmbiguousAlignment,
                ["classEvidenceMs"] = timings["scan_class_evidence"]
            });
        return reliable;
    }

    private static void CopyScanDiagnostics(MapScanDiagnostics source, MapScanDiagnostics target)
    {
        target.ScanCandidateCount = source.ScanCandidateCount;
        target.ScanVerificationCandidateCount = source.ScanVerificationCandidateCount;
        target.ScanVerifiedCandidateCount = source.ScanVerifiedCandidateCount;
        target.ScanVerificationTimedOut = source.ScanVerificationTimedOut;
        target.ScanEarlyExited = source.ScanEarlyExited;
        target.ScanFormalStructureAttemptCount = source.ScanFormalStructureAttemptCount;
        target.ScanFormalStructureAcceptedCount = source.ScanFormalStructureAcceptedCount;
        target.ScanTotalVerificationMilliseconds = source.ScanTotalVerificationMilliseconds;
        target.ScanEffectiveBudgetMilliseconds = source.ScanEffectiveBudgetMilliseconds;
    }
}
