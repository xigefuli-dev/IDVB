using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private List<(SideEntranceScanCandidate Candidate, MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>
        VerifySideEntranceCandidates(CapturedGameFrame frame, IReadOnlyList<SideEntranceScanCandidate> candidates,
            MapRecognitionTuning sideAlignmentTuning, Dictionary<string, double> timings)
    {
        var context = ScanExecutionContext.Current;
        var evidenceFrame = context?.Frame;
        var reliable = new List<(SideEntranceScanCandidate Candidate, MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>();
        if (evidenceFrame is null) return reliable;
        var timer = Stopwatch.StartNew();
        if (context!.Policy.Mode == ScanPerformanceMode.Quality)
        {
            // Compare every in-class member using its own anchors and scale basins. Never borrow
            // the hit map's transform or skip a sibling because it fell below retrieval TopK.
            var hits = candidates.Where(c => c.SearchHypotheses.Any(h =>
                h.StructureIndex is { } index
                && _recognition.TryCreateSideEntranceAlignmentSeed(h, frame.ViewportBounds, out var seed, out _)
                && ScanIdentityVerifier.Verify(evidenceFrame, index, seed.LockedTransform, frame.ViewportBounds, context).State
                    == ScanIdentityState.Supported)).Select(c => c.Map.Id).ToHashSet();
            foreach (var group in context.VariantGroups.Where(g => g.Any(hits.Contains)))
            foreach (var variant in candidates.Where(c => group.Contains(c.Map.Id)))
            {
                var refined = SideEntranceScanPipeline.RefineVariant(variant, evidenceFrame, frame.ViewportBounds, context);
                if (refined.Count == variant.SearchHypotheses.Count && refined.Count > 0)
                    variant.SearchHypotheses = refined;
            }
        }
        var completed = 0;
        var formal = 0;
        using var budget = MapNoDoorAlignmentBudgetContext.Enter(() => Math.Max(0, context!.RemainingMilliseconds - 60));
        foreach (var candidate in candidates)
        {
            if (!context!.CanCompute) break;
            var hypotheses = candidate.SearchHypotheses.Count > 0 ? candidate.SearchHypotheses : new[] { candidate };
            var complete = true;
            var supported = false;
            ScanIdentityEvidence? bestEvidence = null;
            MapAlignmentSession? bestSeed = null;
            MapRecognitionAttempt? bestAttempt = null;
            foreach (var hypothesis in hypotheses)
            {
                if (!context.CanCompute) { complete = false; break; }
                if (hypothesis.StructureIndex is not { } index
                    || !_recognition.TryCreateSideEntranceAlignmentSeed(hypothesis, frame.ViewportBounds, out var seed, out _))
                { complete = false; continue; }
                var evidence = ScanIdentityVerifier.Verify(evidenceFrame, index, seed.LockedTransform, frame.ViewportBounds, context);
                candidate.IdentityEvidence = evidence;
                if (evidence.State == ScanIdentityState.Unverified) { complete = false; break; }
                if (evidence.State != ScanIdentityState.Supported) continue;
                LogScanVerificationCandidateSelected(hypothesis, completed);
                var structureTuning = CreateScanVerificationTuning(
                    MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                        CreateStructureTuningForFloor(candidate.Map, candidate.FloorKey, CreateInitialAlignmentStructureTuning())));
                structureTuning.StructureFallbackBudgetMilliseconds = Math.Max(1, Math.Min(
                    structureTuning.StructureFallbackBudgetMilliseconds, context.RemainingMilliseconds - 60));
                var attempt = RunMandatoryCandidateStructureRegistration(frame, hypothesis, seed,
                    sideAlignmentTuning, structureTuning, out seed);
                formal++;
                if (attempt.Recognition?.Result.OverlayTransform is not { } finalTransform || !attempt.StructureAccepted)
                {
                    candidate.IdentityEvidence = ScanIdentityEvidence.Unverified("alignment-not-confirmed");
                    complete = false;
                    continue;
                }
                // Re-evaluate the transform that will actually be consumed, including rescue/precision changes.
                evidence = ScanIdentityVerifier.Verify(evidenceFrame, index, finalTransform, frame.ViewportBounds, context);
                candidate.IdentityEvidence = evidence;
                if (evidence.State == ScanIdentityState.Unverified) { complete = false; break; }
                if (evidence.State != ScanIdentityState.Supported) continue;
                if (bestEvidence is null || evidence.ForwardMeanPixels < bestEvidence.ForwardMeanPixels)
                {
                    bestEvidence = evidence;
                    bestSeed = seed;
                    bestAttempt = attempt;
                }
                supported = true;
                if (context.Policy.Mode != ScanPerformanceMode.Quality) break;
            }
            if (supported && complete && bestEvidence is not null)
            {
                SideEntranceCandidateEvidence.ApplyStructureAttempt(candidate, bestAttempt!);
                candidate.IdentityEvidence = bestEvidence;
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
                if (!complete) candidate.IdentityEvidence = ScanIdentityEvidence.Unverified("verification-incomplete");
                candidate.RejectionDetail = candidate.IdentityEvidence.Reason;
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
                    ["reason"] = candidate.IdentityEvidence.Reason
                });
            _scanProgressOverlay.Report(.76 + .12 * completed / candidates.Count, "正在比较地图结构...");
        }
        timings["scan_verification"] = timer.Elapsed.TotalMilliseconds;
        _lastDiagnostics!.ScanCandidateCount = candidates.Count;
        _lastDiagnostics.ScanVerificationCandidateCount = candidates.Count;
        _lastDiagnostics.ScanVerifiedCandidateCount = completed;
        _lastDiagnostics.ScanVerificationTimedOut = !context!.CanCompute;
        _lastDiagnostics.ScanEarlyExited = false;
        _lastDiagnostics.ScanFormalStructureAttemptCount = formal;
        _lastDiagnostics.ScanFormalStructureAcceptedCount = reliable.Count;
        _lastDiagnostics.ScanTotalVerificationMilliseconds = timer.Elapsed.TotalMilliseconds;
        _lastDiagnostics.ScanEffectiveBudgetMilliseconds = context.Policy.BudgetMilliseconds;
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
