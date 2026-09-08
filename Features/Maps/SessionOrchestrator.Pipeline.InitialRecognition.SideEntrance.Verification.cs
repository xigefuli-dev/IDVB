using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private List<(SideEntranceScanCandidate Candidate,
        MapAlignmentSession Seed,
        MapRecognitionAttempt Attempt)> VerifySideEntranceCandidates(
        CapturedGameFrame frame,
        IReadOnlyList<SideEntranceScanCandidate> candidates,
        MapRecognitionTuning sideAlignmentTuning,
        Dictionary<string, double> sideTimings)
    {
        var reliable = new List<(SideEntranceScanCandidate Candidate,
            MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>();
        var verificationCandidates = candidates;
        _lastDiagnostics!.ScanVerificationCandidateCount =
            verificationCandidates.Count;

        var verifiedCount = 0;
        var scanVerificationStopwatch = Stopwatch.StartNew();
        var scanVerificationTimedOut = false;
        var scanEarlyExited = false;
        var scanEarlyExitReason = string.Empty;
        var scanCheapRejectCount = 0;
        var scanCheapRejectMilliseconds = 0d;
        var scanFormalStructureAttemptCount = 0;
        var scanFormalStructureCompletedCount = 0;
        var scanFormalStructureAcceptedCount = 0;
        var scanShadowPairCount = 0;
        var scanShadowTrueFormalFalseCount = 0;
        var scanShadowFalseFormalTrueCount = 0;
        var scanShadowTrueFormalTrueCount = 0;
        var scanShadowFalseFormalFalseCount = 0;
        var scanVpsgAttemptCount = 0;
        var scanFullRecoveryCount = 0;
        var scanTemplateValidationMilliseconds = 0d;
        var scanVpsgMilliseconds = 0d;
        var scanStructureMilliseconds = 0d;
        var candidate0TemplateMilliseconds = 0d;
        var candidate0VpsgMilliseconds = 0d;
        var candidate0StructureMilliseconds = 0d;
        var scanShadowCollectionEnabled = _settings!
            .StructureRegistrationTuning.EnableScanCheapRejectShadowCollection;
        var scanEffectiveBudgetMilliseconds = scanShadowCollectionEnabled
            ? MapOpenAlignmentRouteRules
                .ScanVerificationShadowCollectionBudgetMilliseconds
            : MapOpenAlignmentRouteRules.ScanVerificationBudgetMilliseconds;

        void ApplyScanDiagnostics(MapScanDiagnostics diagnostics)
        {
            diagnostics.ScanCandidateCount = candidates.Count;
            diagnostics.ScanVerificationCandidateCount =
                verificationCandidates.Count;
            diagnostics.ScanCheapRejectCount = scanCheapRejectCount;
            diagnostics.ScanCheapRejectMilliseconds =
                scanCheapRejectMilliseconds;
            diagnostics.ScanFormalStructureAttemptCount =
                scanFormalStructureAttemptCount;
            diagnostics.ScanFormalStructureCompletedCount =
                scanFormalStructureCompletedCount;
            diagnostics.ScanFormalStructureAcceptedCount =
                scanFormalStructureAcceptedCount;
            diagnostics.ScanShadowPairCount = scanShadowPairCount;
            diagnostics.ScanShadowTrueFormalFalseCount =
                scanShadowTrueFormalFalseCount;
            diagnostics.ScanShadowFalseFormalTrueCount =
                scanShadowFalseFormalTrueCount;
            diagnostics.ScanShadowTrueFormalTrueCount =
                scanShadowTrueFormalTrueCount;
            diagnostics.ScanShadowFalseFormalFalseCount =
                scanShadowFalseFormalFalseCount;
            diagnostics.ScanShadowCollectionEnabled =
                scanShadowCollectionEnabled;
            diagnostics.ScanEffectiveBudgetMilliseconds =
                scanEffectiveBudgetMilliseconds;
            diagnostics.ScanVpsgAttemptCount = scanVpsgAttemptCount;
            diagnostics.ScanFullRecoveryCount = scanFullRecoveryCount;
            diagnostics.ScanTotalVerificationMilliseconds =
                scanVerificationStopwatch.Elapsed.TotalMilliseconds;
            diagnostics.ScanCandidate0TemplateValidationMilliseconds =
                candidate0TemplateMilliseconds;
            diagnostics.ScanCandidate0VpsgMilliseconds =
                candidate0VpsgMilliseconds;
            diagnostics.ScanCandidate0StructureMilliseconds =
                candidate0StructureMilliseconds;
        }

        using var scanBudgetLease = MapNoDoorAlignmentBudgetContext.Enter(
            () => Math.Max(
                0,
                scanEffectiveBudgetMilliseconds
                - (int)Math.Ceiling(
                    scanVerificationStopwatch.Elapsed.TotalMilliseconds)));

        foreach (var (candidate, candidateIndex) in verificationCandidates
            .Select((candidate, index) => (candidate, index)))
        {
            if (scanVerificationStopwatch.ElapsedMilliseconds >= scanEffectiveBudgetMilliseconds)
            {
                scanVerificationTimedOut = true;
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    "扫描结构验证已达时间预算上限，提前终止后续候选验证",
                    details: new()
                    {
                        ["elapsedMs"] = scanVerificationStopwatch.ElapsedMilliseconds,
                        ["budgetMs"] = scanEffectiveBudgetMilliseconds,
                        ["verifiedCount"] = verifiedCount,
                        ["totalCandidates"] = verificationCandidates.Count
                    });
                break;
            }
            LogScanVerificationCandidateSelected(candidate, candidateIndex);
            var candidateAlignment = MapOperationTraceAmbient.StartTopLevel(
                "selected_candidate_alignment",
                MapOperationWaitKind.Compute,
                mapId: candidate.Map.Id.ToString("D"),
                floorKey: candidate.FloorKey,
                attemptIndex: candidateIndex);
            var isReliable = false;
            try
            {
                if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                        candidate,
                        frame.ViewportBounds,
                        out var candidateSeed,
                        out var seedReason))
                {
                    candidateSeed = CreateIndependentCandidateStructureSeed(
                        candidate);
                    LogScanVerificationSeedCreated(
                        candidate,
                        candidateIndex,
                        success: false,
                        candidateSeed,
                        seedReason);
                }
                else
                    LogScanVerificationSeedCreated(
                        candidate,
                        candidateIndex,
                        success: true,
                        candidateSeed,
                        seedReason: string.Empty);

                var sideStructureTuning = CreateScanVerificationTuning(
                    MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                        CreateStructureTuningForFloor(
                            candidate.Map,
                            candidate.FloorKey,
                            CreateInitialAlignmentStructureTuning())));
                var attempt = RunMandatoryCandidateStructureRegistration(
                    frame,
                    candidate,
                    candidateSeed,
                    sideAlignmentTuning,
                    sideStructureTuning,
                    out candidateSeed);
                scanCheapRejectCount += attempt.Diagnostics.ScanCheapRejected
                    ? 1
                    : 0;
                scanCheapRejectMilliseconds +=
                    attempt.Diagnostics.ScanCheapRejectMilliseconds;
                scanFormalStructureAttemptCount += attempt.Diagnostics
                    .ScanFormalStructureAttemptCount;
                scanFormalStructureCompletedCount++;
                scanFormalStructureAcceptedCount += attempt.StructureAccepted ? 1 : 0;
                scanShadowPairCount += attempt.Diagnostics.ScanShadowPairCount;
                scanShadowTrueFormalFalseCount += attempt.Diagnostics
                    .ScanShadowTrueFormalFalseCount;
                scanShadowFalseFormalTrueCount += attempt.Diagnostics
                    .ScanShadowFalseFormalTrueCount;
                scanShadowTrueFormalTrueCount += attempt.Diagnostics
                    .ScanShadowTrueFormalTrueCount;
                scanShadowFalseFormalFalseCount += attempt.Diagnostics
                    .ScanShadowFalseFormalFalseCount;
                scanVpsgAttemptCount += attempt.Diagnostics.ScanVpsgAttempted
                    ? 1
                    : 0;
                scanFullRecoveryCount += attempt.Diagnostics.ScanFullRecoveryAttempted
                    ? 1
                    : 0;
                scanTemplateValidationMilliseconds += attempt.Diagnostics
                    .ScanTemplateValidationMilliseconds;
                scanVpsgMilliseconds += attempt.Diagnostics
                    .ScanVpsgMilliseconds;
                scanStructureMilliseconds += attempt.Diagnostics
                    .ScanStructureMilliseconds;
                if (candidateIndex == 0)
                {
                    candidate0TemplateMilliseconds = attempt.Diagnostics
                        .ScanTemplateValidationMilliseconds;
                    candidate0VpsgMilliseconds = attempt.Diagnostics
                        .ScanVpsgMilliseconds;
                    candidate0StructureMilliseconds = attempt.Diagnostics
                        .ScanStructureMilliseconds;
                }
                isReliable = SideEntranceCandidateEvidence.ApplyStructureAttempt(
                    candidate,
                    attempt);
                if (isReliable)
                {
                    reliable.Add((candidate, candidateSeed, attempt));
                }

                RecordResearchAttemptForMap(
                    candidate.Map,
                    candidate.FloorKey,
                    frame,
                    attempt,
                    "side-entrance-candidate-verification");

                // 结构验证是扫描中最慢的一段（逐候选做 VPSG/结构配准），
                // 侧门扫描回调在 76% 处结束；这里逐候选实时推进，避免进度条停滞。
                verifiedCount++;
                _scanProgressOverlay.Report(
                    0.76d + 0.12d * verifiedCount / verificationCandidates.Count,
                    "正在验证地图结构...");
            }
            finally
            {
                candidateAlignment.Complete();
            }

            // 早停机制：在非 shadow 收集模式下，只要候选通过严格结构验证且定位有效，
            // 即代表当前地图结构完全吻合，无需强行跑满后续无意义候选。
            if (!scanShadowCollectionEnabled && isReliable)
            {
                scanEarlyExited = true;
                scanEarlyExitReason = $"候选 #{candidateIndex} ({candidate.Map.DisplayName}) 已通过严格结构验证，提前终止后续候选验证";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Info,
                    scanEarlyExitReason,
                    details: new()
                    {
                        ["selectedIndex"] = candidateIndex,
                        ["mapSequence"] = candidate.Map.SequenceNumber,
                        ["floorKey"] = candidate.FloorKey,
                        ["templateScore"] = candidate.MatchScore,
                        ["structureScore"] = candidate.StructureScore,
                        ["elapsedMs"] = scanVerificationStopwatch.ElapsedMilliseconds
                    });
                break;
            }
        }
        scanVerificationStopwatch.Stop();
        _scanProgressOverlay.Report(0.88d, "结构验证完成");
        sideTimings["scan_verification"] =
            scanVerificationStopwatch.Elapsed.TotalMilliseconds;
        sideTimings["scan_template_validation"] =
            scanTemplateValidationMilliseconds;
        sideTimings["scan_vpsg"] = scanVpsgMilliseconds;
        sideTimings["scan_structure"] = scanStructureMilliseconds;
        ApplyScanDiagnostics(_lastDiagnostics!);
        if (!scanEarlyExited && !scanVerificationTimedOut && scanFormalStructureAttemptCount != candidates.Count)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Error,
                "扫描候选未获得一对一正式结构配准",
                details: new()
                {
                    ["templateCandidates"] = candidates.Count,
                    ["formalAttempted"] = scanFormalStructureAttemptCount
                });
        }
        _logCollector.Append(
            MapLogCategory.ScanLifecycle,
            MapLogLevel.Info,
            "扫描结构验证完成",
            elapsedMs: _lastDiagnostics.ScanTotalVerificationMilliseconds,
            details: new()
            {
                ["scan_candidate_count"] = candidates.Count,
                ["scan_verification_candidate_count"] =
                    verificationCandidates.Count,
                ["early_exit"] = scanEarlyExited,
                ["early_exit_reason"] = scanEarlyExitReason,
                ["candidate_0_template_validation_ms"] =
                    candidate0TemplateMilliseconds,
                ["candidate_0_vpsg_ms"] = candidate0VpsgMilliseconds,
                ["candidate_0_structure_ms"] =
                    candidate0StructureMilliseconds,
                ["cheap_reject_count"] = scanCheapRejectCount,
                ["cheap_reject_ms"] = scanCheapRejectMilliseconds,
                ["scan_formal_structure_attempt_count"] =
                    scanFormalStructureAttemptCount,
                ["templateCandidates"] = candidates.Count,
                ["formalAttempted"] = scanFormalStructureAttemptCount,
                ["formalCompleted"] = scanFormalStructureCompletedCount,
                ["formalAccepted"] = scanFormalStructureAcceptedCount,
                ["formalCoverage"] =
                    $"{scanFormalStructureAttemptCount}/{candidates.Count}",
                ["shadow_pair_count"] = scanShadowPairCount,
                ["shadow_true_formal_false"] =
                    scanShadowTrueFormalFalseCount,
                ["shadow_false_formal_true"] =
                    scanShadowFalseFormalTrueCount,
                ["shadow_true_formal_true"] =
                    scanShadowTrueFormalTrueCount,
                ["shadow_false_formal_false"] =
                    scanShadowFalseFormalFalseCount,
                ["scan_total_verification_ms"] =
                    _lastDiagnostics.ScanTotalVerificationMilliseconds,
                ["template_validation_ms"] =
                    scanTemplateValidationMilliseconds,
                ["vpsg_ms"] = scanVpsgMilliseconds,
                ["structure_ms"] = scanStructureMilliseconds,
                ["scan_vpsg_attempt_count"] = scanVpsgAttemptCount,
                ["scan_full_recovery_count"] = scanFullRecoveryCount,
                ["timed_out"] = scanVerificationTimedOut,
                ["budget_ms"] = MapOpenAlignmentRouteRules
                    .ScanVerificationBudgetMilliseconds,
                ["effective_budget_ms"] = scanEffectiveBudgetMilliseconds,
                ["shadow_collection"] = scanShadowCollectionEnabled,
                ["target_p50_ms"] = MapOpenAlignmentRouteRules
                    .ScanVerificationP50Milliseconds,
                ["target_p90_ms"] = MapOpenAlignmentRouteRules
                    .ScanVerificationP90Milliseconds,
                ["target_p99_ms"] = MapOpenAlignmentRouteRules
                    .ScanVerificationP99Milliseconds
            });

        return reliable;
    }

    private static void CopyScanDiagnostics(
        MapScanDiagnostics source,
        MapScanDiagnostics target)
    {
        target.ScanCandidateCount = source.ScanCandidateCount;
        target.ScanVerificationCandidateCount =
            source.ScanVerificationCandidateCount;
        target.ScanCheapRejectCount = source.ScanCheapRejectCount;
        target.ScanCheapRejectMilliseconds =
            source.ScanCheapRejectMilliseconds;
        target.ScanFormalStructureAttemptCount =
            source.ScanFormalStructureAttemptCount;
        target.ScanFormalStructureCompletedCount =
            source.ScanFormalStructureCompletedCount;
        target.ScanFormalStructureAcceptedCount =
            source.ScanFormalStructureAcceptedCount;
        target.ScanShadowPairCount = source.ScanShadowPairCount;
        target.ScanShadowTrueFormalFalseCount =
            source.ScanShadowTrueFormalFalseCount;
        target.ScanShadowFalseFormalTrueCount =
            source.ScanShadowFalseFormalTrueCount;
        target.ScanShadowTrueFormalTrueCount =
            source.ScanShadowTrueFormalTrueCount;
        target.ScanShadowFalseFormalFalseCount =
            source.ScanShadowFalseFormalFalseCount;
        target.ScanShadowCollectionEnabled =
            source.ScanShadowCollectionEnabled;
        target.ScanEffectiveBudgetMilliseconds =
            source.ScanEffectiveBudgetMilliseconds;
        target.ScanVpsgAttemptCount = source.ScanVpsgAttemptCount;
        target.ScanFullRecoveryCount = source.ScanFullRecoveryCount;
        target.ScanTotalVerificationMilliseconds =
            source.ScanTotalVerificationMilliseconds;
        target.ScanCandidate0TemplateValidationMilliseconds =
            source.ScanCandidate0TemplateValidationMilliseconds;
        target.ScanCandidate0VpsgMilliseconds =
            source.ScanCandidate0VpsgMilliseconds;
        target.ScanCandidate0StructureMilliseconds =
            source.ScanCandidate0StructureMilliseconds;
    }
}
