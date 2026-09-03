using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;
namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{
    private void RunInitialSideEntranceRecognition(
        CapturedGameFrame frame,
        InitialRecognitionPipelineState result,
        bool recognizeOnly = false)
    {
        ref var recognition = ref result.Recognition;
        ref var failureReason = ref result.FailureReason;
        ref var pendingChoices = ref result.PendingChoices;
        ref var pendingChoicesReason = ref result.PendingChoicesReason;
        ref var pendingSideEntranceSeed = ref result.PendingSideEntranceSeed;
        ref var pendingSideEntranceIdentity = ref result.PendingSideEntranceIdentity;
        ref var pendingSideEntranceScan = ref result.PendingSideEntranceScan;
        var repairCacheKeys = result.RepairCacheKeys;
        ref var scanSucceeded = ref result.ScanSucceeded;

        // ── 侧门扫描链路：单门特征匹配识别地图 + 侧门对齐 ──
        // 侧门场景通常只有 1 扇门可见，双门几何排名（RankGeometry 硬性
        // 要求 ≥2 门）必然失败。改用侧门特征模板匹配识别地图身份，
        // 生成对齐种子后走 SideEntrance 对齐（单门 + 结构配准）。
        MapRecognitionAttempt sideAttempt;
        MapAlignmentSession? seed = null;
        var sideMapId = Guid.Empty;
        var displayName = string.Empty;
        var sideTimings = new Dictionary<string, double>();
        MapOperationTrace.MapOperationSpanScope? initialPostProcess = null;
        try
        {
            var sideSw = Stopwatch.StartNew();
            var initialRecognition = MapOperationTraceAmbient.StartTopLevel(
                "initial_recognition",
                MapOperationWaitKind.Compute);
            SideEntranceScanResult sideScan;
            try
            {
                sideScan = _recognition.RunSideEntranceScan(
                    frame,
                    _settings!.RecognitionTuning,
                    topK: 5,
                    mapClass: _matchSession.Snapshot.MapClass,
                    // 进度由门检测、每张地图的粗搜及精化完成数实时驱动。
                    progress: value => _scanProgressOverlay.Report(
                        0.38d + value * 0.38d,
                        "正在扫描地图特征..."));
            }
            finally
            {
                initialRecognition.Complete();
            }
            initialPostProcess = MapOperationTraceAmbient.StartTopLevel(
                "initial_recognition",
                MapOperationWaitKind.Compute);
            pendingSideEntranceScan = sideScan;
            _lastDiagnostics = new MapScanDiagnostics
            {
                ReadyMapCount = _recognition.ReadyMapCount,
                TotalMapCount = _recognition.TotalMapCount,
                SideEntranceReadyMapCount = sideScan.ReadyMapCount,
                SideEntranceEligibleMapCount = sideScan.EligibleMapCount,
                SideEntranceRejectedCandidateCount =
                    sideScan.RejectedCandidateCount,
                ScanCandidateCount = sideScan.Candidates.Count
            };
            var candidates = sideScan.Candidates;
            sideTimings["side_entrance_scan"] = sideSw.Elapsed.TotalMilliseconds;
            sideTimings["gate_detection"] = sideScan.GateDetection.ElapsedMilliseconds;
            _lastScanPhaseTimings = sideTimings;
            if (sideScan.GateDetection.Gates.Count == 0)
            {
                failureReason =
                    "识别失败：侧门扫描要求当前地图暴露一个门特征，但未检测到门";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
                initialPostProcess.Complete();
                return;
            }
            if (candidates.Count == 0)
            {
                failureReason =
                    $"识别失败：已检测到门，但{sideScan.FailureReason}";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
                initialPostProcess.Complete();
                return;
            }

            // The scan is triggered while the native game map is
            // already open. Synchronize that fact before the next
            // physical close/reopen key pair; otherwise the first
            // key after scanning is interpreted against the stale
            // pre-scan toggle state.
            scanSucceeded = true;

            // Raw side-template similarity is only a retrieval clue. Strict
            // per-candidate structure validation remains the default, while
            // the explicit scan setting can defer it until player selection.
            var requireStrictStructureRegistration = _settings!
                .RequireStrictStructureRegistrationDuringScan;
            var sideAlignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (sideAlignmentTuning.GateTemplateThreshold
                > GateTemplateRules.FallbackPairThreshold)
            {
                sideAlignmentTuning.GateTemplateThreshold =
                    GateTemplateRules.FallbackPairThreshold;
            }
            var reliable = new List<(SideEntranceScanCandidate Candidate,
                MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>();
            var verificationCandidates = requireStrictStructureRegistration
                ? SideEntranceCandidateEvidence.SelectVerificationCandidates(candidates)
                : [];
            _lastDiagnostics.ScanVerificationCandidateCount =
                verificationCandidates.Count;
            if (!requireStrictStructureRegistration)
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "扫描已按设置跳过严格结构配准；候选将在玩家选择后对齐。");

            initialPostProcess.Complete();
            var verifiedCount = 0;
            var scanVerificationStopwatch = Stopwatch.StartNew();
            var scanVerificationTimedOut = false;
            var scanCheapRejectCount = 0;
            var scanCheapRejectMilliseconds = 0d;
            var scanFormalStructureAttemptCount = 0;
            var scanShadowPairCount = 0;
            var scanShadowTrueFormalFalseCount = 0;
            var scanShadowFalseFormalTrueCount = 0;
            var scanShadowTrueFormalTrueCount = 0;
            var scanShadowFalseFormalFalseCount = 0;
            var scanVpsgAttemptCount = 0;
            var scanTemplateValidationMilliseconds = 0d;
            var scanVpsgMilliseconds = 0d;
            var scanStructureMilliseconds = 0d;
            var candidate0TemplateMilliseconds = 0d;
            var candidate0VpsgMilliseconds = 0d;
            var candidate0StructureMilliseconds = 0d;
            var scanShadowCollectionEnabled = requireStrictStructureRegistration
                && _settings.StructureRegistrationTuning.EnableScanCheapRejectShadowCollection;
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
                diagnostics.ScanFullRecoveryCount = 0;
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
                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                        is not { } remaining
                    || remaining < MapOpenAlignmentRouteRules
                        .ScanVerificationMinimumCandidateBudgetMilliseconds)
                {
                    scanVerificationTimedOut = reliable.Count == 0;
                    break;
                }
                LogScanVerificationCandidateSelected(candidate, candidateIndex);
                var candidateAlignment = MapOperationTraceAmbient.StartTopLevel(
                    "selected_candidate_alignment",
                    MapOperationWaitKind.Compute,
                    mapId: candidate.Map.Id.ToString("D"),
                    floorKey: candidate.FloorKey,
                    attemptIndex: candidateIndex);
                try
                {
                    if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                            candidate,
                            frame.ViewportBounds,
                            out var candidateSeed,
                            out var seedReason))
                    {
                        candidate.RejectionReason =
                            SideEntranceRejectionReason.InvalidFeatureData;
                        candidate.RejectionDetail = seedReason;
                        LogScanVerificationSeedCreated(
                            candidate,
                            candidateIndex,
                            success: false,
                            seed: null,
                            seedReason);
                        continue;
                    }
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
                    var attempt = AlignSideEntranceWithScaleFallback(
                        frame,
                        candidate,
                        candidateSeed,
                        sideAlignmentTuning,
                        sideStructureTuning,
                        allowVpsgRescue: true,
                        out candidateSeed);
                    scanCheapRejectCount += attempt.Diagnostics.ScanCheapRejected
                        ? 1
                        : 0;
                    scanCheapRejectMilliseconds +=
                        attempt.Diagnostics.ScanCheapRejectMilliseconds;
                    scanFormalStructureAttemptCount += attempt.Diagnostics
                        .ScanFormalStructureAttemptCount;
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
                    if (SideEntranceCandidateEvidence.ApplyStructureAttempt(
                            candidate,
                            attempt))
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
            }
            scanVerificationStopwatch.Stop();
            sideTimings["scan_verification"] =
                scanVerificationStopwatch.Elapsed.TotalMilliseconds;
            sideTimings["scan_template_validation"] =
                scanTemplateValidationMilliseconds;
            sideTimings["scan_vpsg"] = scanVpsgMilliseconds;
            sideTimings["scan_structure"] = scanStructureMilliseconds;
            ApplyScanDiagnostics(_lastDiagnostics);
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                scanVerificationTimedOut
                    ? MapLogLevel.Warning
                    : MapLogLevel.Info,
                scanVerificationTimedOut
                    ? "扫描结构验证达到硬预算，停止继续验证"
                    : "扫描结构验证完成",
                elapsedMs: _lastDiagnostics.ScanTotalVerificationMilliseconds,
                details: new()
                {
                    ["scan_candidate_count"] = candidates.Count,
                    ["scan_verification_candidate_count"] =
                        verificationCandidates.Count,
                    ["candidate_0_template_validation_ms"] =
                        candidate0TemplateMilliseconds,
                    ["candidate_0_vpsg_ms"] = candidate0VpsgMilliseconds,
                    ["candidate_0_structure_ms"] =
                        candidate0StructureMilliseconds,
                    ["cheap_reject_count"] = scanCheapRejectCount,
                    ["cheap_reject_ms"] = scanCheapRejectMilliseconds,
                    ["scan_formal_structure_attempt_count"] =
                        scanFormalStructureAttemptCount,
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
                    ["scan_full_recovery_count"] = 0,
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

            var orderedReliable = SideEntranceCandidateEvidence.OrderVerified(
                    reliable,
                    item => item.Candidate)
                .ToArray();
            var choices = BuildScanVerificationChoices(
                orderedReliable,
                candidates,
                frame,
                requireStrictStructureRegistration,
                out var referenceCandidates);

            // Ambiguity is a valid empty-recognition outcome. Never promote
            // the highest template maximum merely to fill the chooser.
            if (reliable.Count != 1
                || _settings.RecognitionTuning.ForceCandidateSelection
                || _settings.CandidateDecisionMode
                    != MapCandidateDecisionMode.Traditional)
            {
                pendingChoices = choices;
                pendingChoicesReason = !requireStrictStructureRegistration
                    ? $"扫描阶段未执行严格结构配准；以下 {referenceCandidates.Length} 项按模板相似度排序，选择后再执行结构对齐。"
                    : reliable.Count == 0
                    ? $"0 个已验证结果；以下 {referenceCandidates.Length} 项仅供参考，点击后仍会执行严格结构复核。"
                    : $"{reliable.Count} 个已验证结果；已验证结果优先，另有 {referenceCandidates.Length} 项仅供参考。";
                failureReason = requireStrictStructureRegistration
                    && reliable.Count == 0
                    ? $"侧门扫描无可靠候选（侧门就绪 {sideScan.ReadyMapCount}/{sideScan.EligibleMapCount}）。"
                    : null;
                initialPostProcess.Complete();
                return;
            }

            var selected = orderedReliable[0];
            var best = selected.Candidate;
            ApplyScanDiagnostics(selected.Attempt.Diagnostics);
            sideAttempt = selected.Attempt;
            seed = selected.Seed;
            displayName = best.Map.DisplayName;
            sideMapId = best.Map.Id;
            pendingSideEntranceSeed = seed;
            pendingSideEntranceIdentity = sideAttempt.Recognition;
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"侧门可靠候选 · map={best.Map.SequenceNumber}#{best.FloorKey} · "
                + $"template={best.MatchScore:P0} · structure={best.StructureScore:P0} · "
                + $"identity={best.IdentityConfidence:P0}");
        }
        catch (Exception alignEx)
        {
            initialPostProcess?.Complete();
            RecordResearchAttemptForMap(
                _recognition.TryGetMap(sideMapId), seed?.FloorKey, frame,
                new MapRecognitionAttempt { FailureReason = alignEx.Message },
                "side-entrance");
            failureReason = $"侧门对齐异常：{alignEx.Message}";
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Error,
                failureReason,
                details: new()
                {
                    ["exceptionType"] = alignEx.GetType().FullName,
                    ["stackTrace"] = alignEx.ToString()
                });
            return;
        }

        _lastDiagnostics = sideAttempt.Diagnostics;
        _lastDiagnostics.SideEntranceReadyMapCount =
            pendingSideEntranceScan?.ReadyMapCount ?? 0;
        _lastDiagnostics.SideEntranceEligibleMapCount =
            pendingSideEntranceScan?.EligibleMapCount ?? 0;
        _lastDiagnostics.SideEntranceRejectedCandidateCount =
            pendingSideEntranceScan?.RejectedCandidateCount ?? 0;
        _lastScanPhaseTimings = sideTimings;
        RecordResearchAttemptForMap(
            sideAttempt.Recognition?.Map
                ?? _recognition.TryGetMap(sideMapId),
            seed?.FloorKey, frame, sideAttempt, "side-entrance");

        _logCollector.Append(
            MapLogCategory.Session,
            sideAttempt.Recognition is null ? MapLogLevel.Warning : MapLogLevel.Info,
            $"侧门对齐完成 · success={sideAttempt.Recognition is not null} · "
            + $"reason={sideAttempt.FailureReason ?? "<none>"}",
            details: new()
            {
                ["mapId"] = sideMapId,
                ["confidence"] = sideAttempt.Recognition?.Result.Confidence,
                ["failureReason"] = sideAttempt.FailureReason
            });

        if (sideAttempt.Recognition is { } sideRec)
        {
            recognition = sideRec;
            // 后台扫描（recognizeOnly）：只产出身份 + 侧门种子，延迟到开图
            // 消费时再提交对齐。不锁定 _lastRecognition / 不提交可靠会话，
            // 防止劫持手动识别；_statusMessage 由 CompleteBackgroundScan 设置。
            if (recognizeOnly)
                return;
            _lastRecognition = sideRec;
            _currentFloorKey = sideRec.Result.Floor;
            _mapLease.Bind(_matchSession.Snapshot, sideRec.Map.Id);
            // 用侧门扫描种子（而非 null）作为 previous，保留
            // SideEntranceScanPriorConfidence，使后续仅对齐调用
            // 能正确识别侧门路由（AllowScaleSearch = true）。
            _lastAlignmentSession = UpdateAlignmentSession(
                seed,
                sideRec);
            RememberPrimaryFloorSession(sideRec, _lastAlignmentSession);
            _statusMessage =
                $"侧门对齐成功：{displayName} · 置信度 {sideRec.Result.Confidence:P0}";
        }
        else if (sideAttempt.Choices.Count > 0)
        {
            pendingChoices = sideAttempt.Choices;
            pendingChoicesReason =
                sideAttempt.FailureReason ?? string.Empty;
        }
        else
        {
            failureReason = $"侧门对齐失败：{sideAttempt.FailureReason}";
        }
    }
}
