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

            // Template retrieval only determines which identities are worth
            // testing. Every retained candidate has one formal structure pass.
            const bool requireStrictStructureRegistration = true;
            var sideAlignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (sideAlignmentTuning.GateTemplateThreshold
                > GateTemplateRules.FallbackPairThreshold)
            {
                sideAlignmentTuning.GateTemplateThreshold =
                    GateTemplateRules.FallbackPairThreshold;
            }
            var reliable = VerifySideEntranceCandidates(
                frame,
                candidates,
                sideAlignmentTuning,
                sideTimings);

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
                var candidateRouteReason = reliable.Count != 1
                    ? (reliable.Count == 0 ? "无可靠验证候选" : $"存在多个可靠验证候选 (count={reliable.Count})")
                    : _settings.RecognitionTuning.ForceCandidateSelection
                    ? "已启用「强制进入候选界面 (ForceCandidateSelection)」配置"
                    : $"决策模式非传统模式 (mode={_settings.CandidateDecisionMode})";

                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"侧门扫描进入候选选择链路 · reason={candidateRouteReason} · reliableCount={reliable.Count}");

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
            CopyScanDiagnostics(_lastDiagnostics!, selected.Attempt.Diagnostics);
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
            // 后台扫描只产出身份和侧门种子，开图时再提交对齐。
            if (recognizeOnly)
                return;
            _lastRecognition = sideRec;
            _currentFloorKey = sideRec.Result.Floor;
            _mapLease.Bind(_matchSession.Snapshot, sideRec.Map.Id);
            // 保留侧门种子，使后续仅对齐调用继续走侧门尺度搜索。
            _lastAlignmentSession = UpdateAlignmentSession(seed, sideRec);
            RememberPrimaryFloorSession(sideRec, _lastAlignmentSession);
            _statusMessage = $"侧门对齐成功：{displayName} · 置信度 {sideRec.Result.Confidence:P0}";
        }
        else if (sideAttempt.Choices.Count > 0)
        {
            pendingChoices = sideAttempt.Choices;
            pendingChoicesReason = sideAttempt.FailureReason ?? string.Empty;
        }
        else
        {
            failureReason = $"侧门对齐失败：{sideAttempt.FailureReason}";
        }
    }
}
