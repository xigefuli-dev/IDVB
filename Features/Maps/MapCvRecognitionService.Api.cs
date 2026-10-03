using System.Diagnostics;
using IdentityVisionBridge.Vision;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal VisionScanResult ScanForApi(CapturedGameFrame frame, string? mapClass,
        ScanPerformanceMode mode, CancellationToken cancellationToken, long? startedTimestamp = null)
    {
        using var execution = ScanExecutionContext.Enter(mode, cancellationToken, startedTimestamp: startedTimestamp);
        execution.CatalogRevision = CatalogRevision;
        execution.VariantGroups = ScanVariantGroups;
        var scan = RunSideEntranceScan(frame, new MapRecognitionTuning(), mapClass: mapClass);
        var verified = new Dictionary<Guid, RuntimeMapRecognition>();
        var verifiedIndexes = new Dictionary<Guid, ScanStructureIndex>();
        if (execution.Frame is { } observation)
        {
            // Establish every identity's evidence before spending the shared budget on alignment.
            foreach (var candidate in scan.Candidates)
            {
                foreach (var hypothesis in Hypotheses(candidate))
                {
                    if (!execution.CanCompute) break;
                    if (hypothesis.StructureIndex is { } index && TryCreateSideEntranceAlignmentSeed(hypothesis,
                        frame.ViewportBounds, out var seed, out _))
                        hypothesis.IdentityEvidence = ScanIdentityVerifier.Verify(observation, index,
                            seed.LockedTransform, frame.ViewportBounds, execution);
                }
                candidate.IdentityEvidence = Hypotheses(candidate).Select(item => item.IdentityEvidence)
                    .OrderByDescending(item => item.State == ScanIdentityState.Supported)
                    .ThenByDescending(item => item.State == ScanIdentityState.Unverified)
                    .ThenBy(ScanIdentityVerifier.FitCost).First();
            }
            foreach (var candidate in scan.Candidates)
            {
                foreach (var hypothesis in Hypotheses(candidate).OrderBy(item => ScanIdentityVerifier.FitCost(item.IdentityEvidence)))
                {
                    if (!execution.CanCompute) break;
                    if (hypothesis.StructureIndex is not { } index
                        || !ScanIdentityVerifier.ShouldAttemptStructureRegistration(hypothesis.IdentityEvidence, mode)
                        || !TryCreateSideEntranceAlignmentSeed(hypothesis, frame.ViewportBounds, out var seed, out _)) continue;
                    var tuning = MapScanVerificationRules.CreateTuning(
                        MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(new MapStructureRegistrationTuning()));
                    tuning.StructureFallbackBudgetMilliseconds = Math.Max(1, Math.Min(
                        tuning.StructureFallbackBudgetMilliseconds, execution.RemainingMilliseconds - 60));
                    tuning.Normalize();
                    using var budget = MapNoDoorAlignmentBudgetContext.Enter(() => Math.Max(0, execution.RemainingMilliseconds - 60));
                    var recognitionTuning = new MapRecognitionTuning();
                    var attempt = mode == ScanPerformanceMode.DeepScan
                        ? AlignDeepScan(frame, hypothesis, seed, recognitionTuning, tuning)
                        : AlignSideEntrance(frame, hypothesis.Map.Id, seed, MapOverlayAlignmentMode.Uniform,
                            recognitionTuning, tuning,
                            alignmentSearchContext: CreateSideEntranceWarmSearchContext(seed, recognitionTuning,
                                useInitialHighPrecisionRecovery: true), mapClass: mapClass);
                    if (!attempt.StructureAccepted && execution.CanCompute && mode != ScanPerformanceMode.DeepScan)
                    {
                        // Same independently validated VPSG scale recovery used by the desktop scan.
                        var recovery = MapScaleSeedResolver.CreateStrictVpsgValidationTuning(tuning);
                        recovery.StructureFallbackBudgetMilliseconds = Math.Max(1, Math.Min(
                            MapOpenAlignmentRouteRules.ScanVerificationVpsgBudgetMilliseconds, execution.RemainingMilliseconds - 60));
                        recovery.Normalize();
                        var recovered = AlignLockedFloorFeature(frame, hypothesis.Map.Id, hypothesis.FloorKey,
                            seed.LockedTransform, MapOverlayAlignmentMode.Uniform, recognitionTuning, recovery, hypothesis.MatchScore);
                        if (recovered.StructureAccepted) attempt = recovered;
                    }
                    if (!attempt.StructureAccepted || attempt.Recognition?.Result.OverlayTransform is not { } transform)
                    { candidate.RejectionDetail = "formal-alignment:" + attempt.FailureReason; continue; }
                    // A scan seed must also work through the steady-state fast alignment route.
                    if (!TryAlignWithVpsg3(frame, hypothesis.Map, hypothesis.FloorKey, 1, out var fast, out var fastStatus,
                        knownScaleSeed: transform.ScaleX) || !fast.StructureAccepted
                        || fast.Recognition?.Result.OverlayTransform is not { } fastTransform)
                    { candidate.RejectionDetail = "fast-alignment:" + fastStatus.ToString(); continue; }
                    transform = fastTransform;
                    var final = ScanIdentityVerifier.Verify(observation, index, transform, frame.ViewportBounds, execution);
                    if (final.State != ScanIdentityState.Supported)
                    { candidate.RejectionDetail = "final-verification:" + final.Reason; continue; }
                    candidate.IdentityEvidence = final;
                    candidate.VerifiedTransform = transform;
                    candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
                    candidate.RejectionDetail = "";
                    verified[candidate.Map.Id] = fast.Recognition;
                    verifiedIndexes[candidate.Map.Id] = index;
                    break;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var complete = execution.RetrievalCompleted && scan.Candidates.Count == scan.EligibleMapCount;
        var selected = ScanIdentityVerifier.SelectIdentity(scan.Candidates, complete, execution.CanCompute,
            execution.VariantGroups, ScanIdentitySelectionPolicy.RequireUniqueSupport);
        var winner = selected is { } id && verified.TryGetValue(id, out var recognition) ? recognition : null;
        // Publication rechecks the exact frame, current catalog and final transform under the original deadline.
        if (winner is not null && (execution.Frame is not { } evidence || !verifiedIndexes.TryGetValue(winner.Map.Id, out var finalIndex)
            || CatalogRevision != _repository.GetCatalogRevision()
            || ScanIdentityVerifier.Verify(evidence, finalIndex, winner.Result.OverlayTransform!, frame.ViewportBounds, execution).State
                != ScanIdentityState.Supported || !execution.CanCompute)) winner = null;
        var outcome = execution.Expired ? VisionOutcome.TimedOut : winner is not null ? VisionOutcome.Succeeded
            : scan.Candidates.Count > 0 ? VisionOutcome.NeedsSelection : VisionOutcome.Unrecognized;
        return new()
        {
            OperationId = execution.ScanId, Outcome = outcome,
            MapId = winner?.Map.Id, FloorKey = winner?.Result.Floor,
            Confidence = winner?.Result.IdentityConfidence ?? 0,
            Transform = VisionApiMapper.Transform(winner?.Result.OverlayTransform),
            Candidates = Array.AsReadOnly(scan.Candidates.Select(VisionApiMapper.Candidate).ToArray()),
            ElapsedMilliseconds = execution.ElapsedMilliseconds,
            Reason = outcome == VisionOutcome.Succeeded ? "verified-image-result"
                : execution.Expired ? "deadline-exceeded" : !complete ? "retrieval-incomplete"
                : !string.IsNullOrWhiteSpace(scan.FailureReason) ? scan.FailureReason : "identity-or-alignment-not-confirmed"
        };
    }

    private static IReadOnlyList<SideEntranceScanCandidate> Hypotheses(SideEntranceScanCandidate candidate) =>
        candidate.SearchHypotheses.Count > 0 ? candidate.SearchHypotheses : new[] { candidate };
}
