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
            _lastSideEntranceScan = sideScan;
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
                initialPostProcess = null;
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
                initialPostProcess = null;
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
            initialPostProcess.Complete();
            initialPostProcess = null;

            var reliable = VerifySideEntranceCandidates(frame, candidates, sideAlignmentTuning, sideTimings);
            var context = ScanExecutionContext.Current;
            var selectedId = ScanIdentityVerifier.SelectIdentity(candidates,
                context?.RetrievalCompleted == true && candidates.Count == sideScan.EligibleMapCount,
                context?.CanCompute == true, context?.VariantGroups);
            var choices = BuildScanVerificationChoices(reliable, candidates, frame,
                requireStrictStructureRegistration, out _);
            if (selectedId is null)
            {
                var diagnosticPath = MapDiagnosticModeCapture.WriteUnresolvedScan(
                    frame, context?.Frame, candidates,
                    context?.Policy.Mode ?? ScanPerformanceMode.Balanced);
                if (diagnosticPath is not null)
                    _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                        "未确定身份的扫描原始证据已保存",
                        details: new() { ["path"] = diagnosticPath });
                pendingChoices = choices;
                pendingChoicesReason = "地图尚未确定：当前类别中仍有未排除的竞争结果，或可见结构证据不足。";
                failureReason = pendingChoicesReason;
                return;
            }
            var selected = reliable.First(item => item.Candidate.Map.Id == selectedId);
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
            initialPostProcess = null;
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
