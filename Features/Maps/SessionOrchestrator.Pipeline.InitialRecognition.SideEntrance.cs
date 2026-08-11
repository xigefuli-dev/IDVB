// IDVB Remaster — Session Orchestrator 识别管线

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
        InitialRecognitionPipelineState result)
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
        try
        {
            var sideSw = Stopwatch.StartNew();
            var sideScan = _recognition.RunSideEntranceScan(
                frame,
                _settings!.RecognitionTuning,
                topK: 5,
                mapClass: _matchSession.Snapshot.MapClass);
            pendingSideEntranceScan = sideScan;
            var candidates = sideScan.Candidates;
            sideTimings["side_entrance_scan"] = sideSw.Elapsed.TotalMilliseconds;
            sideTimings["gate_detection"] = sideScan.GateDetection.ElapsedMilliseconds;
            _lastScanPhaseTimings = sideTimings;
            var sideGate = sideScan.Gate;
            if (sideGate is null)
            {
                failureReason =
                    "识别失败：侧门扫描要求当前地图暴露一个门特征，但未检测到门";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
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
                return;
            }

            // The scan is triggered while the native game map is
            // already open. Synchronize that fact before the next
            // physical close/reopen key pair; otherwise the first
            // key after scanning is interpreted against the stale
            // pre-scan toggle state.
            scanSucceeded = true;

            var best = candidates[0];
            displayName = best.Map.DisplayName;
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"侧门扫描选中候选 · map={best.Map.SequenceNumber}#{best.FloorKey} · "
                + $"score={best.MatchScore:P0} · scale={best.MatchScale:F3}",
                details: new()
                {
                    ["matchScore"] = best.MatchScore,
                    ["matchScale"] = best.MatchScale,
                    ["floorKey"] = best.FloorKey
                });

            // Scan only creates map choices. Do not run selected-map
            // alignment for every candidate before the user chooses.
            if (_settings.RecognitionTuning.ForceCandidateSelection
                && candidates.Count > 1)
            {
                var sideChoiceList = new List<MapRecognitionChoice>();
                foreach (var sideCandidate in candidates.Take(5))
                {
                    if (_recognition.TryCreateSideEntranceSelection(
                            sideCandidate,
                            sideGate,
                            frame.ViewportBounds,
                            out var selection,
                            out _,
                            out _))
                    {
                        sideChoiceList.Add(new MapRecognitionChoice
                        {
                            Recognition = selection,
                            VectorError = 0d
                        });
                    }
                }

                if (sideChoiceList.Count > 1)
                {
                    pendingChoices = sideChoiceList;
                    pendingChoicesReason =
                        "强制候选模式已开启，请选择本局地图。";
                    return;
                }
            }

            if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                    best,
                    sideGate,
                    frame.ViewportBounds,
                    out seed,
                    out var seedReason))
            {
                failureReason = $"识别失败：侧门扫描种子无效 ({seedReason})";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
                return;
            }

            // The seed is a one-gate scan observation. It is not
            // committed until the selected-map structure alignment
            // below succeeds.
            pendingSideEntranceSeed = seed;
            sideMapId = seed.MapId;
            if (_recognition.TryCreateSideEntranceSelection(
                    best,
                    sideGate,
                    frame.ViewportBounds,
                    out var provisionalIdentity,
                    out _,
                    out _))
            {
                pendingSideEntranceIdentity = provisionalIdentity;
            }

            var sideAlignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (sideAlignmentTuning.GateTemplateThreshold
                > GateTemplateRules.FallbackPairThreshold)
            {
                sideAlignmentTuning.GateTemplateThreshold =
                    GateTemplateRules.FallbackPairThreshold;
            }
            // 侧门扫描已通过多尺度特征匹配确定门的缩放倍率
            // （seed.BaselineGateScale = 特征模板的 MatchScale）。门图标与
            // 侧门特征裁自同一识别图、共用同一放大倍率，因此直接把该
            // 尺度作为门检测的 warm scale，走窄带 WarmScaleSearch（约 7
            // 个尺度），而不是无尺度先验的 FullSearch（约 15 个尺度全帧
            // 扫描）。即使 WarmScaleSearch 找不到门，侧门路径也只回退到
            // 单门/结构配准，绝不升级 FullSearch。
            var sideSearchContext =
                CreateSideEntranceSearchContext(
                    seed,
                    sideAlignmentTuning,
                    useInitialHighPrecisionRecovery: true);

            var sideStructureTuning =
                CreateInitialAlignmentStructureTuning();
            var sideMap = _recognition.TryGetMap(sideMapId);
            MapFeatureCacheKey? sideRepairKey = null;
            sideAttempt = sideMap is null
                ? _recognition.AlignSideEntrance(
                    frame,
                    sideMapId,
                    seed,
                    _settings.OverlayAlignmentMode,
                    sideAlignmentTuning,
                    sideStructureTuning,
                    alignmentSearchContext: sideSearchContext)
                : AlignUsingScaleCache(
                    frame,
                    sideMap,
                    seed.FloorKey,
                    sideAlignmentTuning,
                    sideStructureTuning,
                    seed.SideEntranceScanPriorConfidence,
                    () => _recognition.AlignSideEntrance(
                        frame,
                        sideMapId,
                        seed,
                        _settings.OverlayAlignmentMode,
                        sideAlignmentTuning,
                        sideStructureTuning,
                        alignmentSearchContext: sideSearchContext),
                    out sideRepairKey);
            if (sideRepairKey is not null)
                repairCacheKeys[sideMapId] = sideRepairKey;
        }
        catch (Exception alignEx)
        {
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
            _lastRecognition = sideRec;
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
        return;
    }

}
