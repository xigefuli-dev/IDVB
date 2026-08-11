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
    private void RunInitialDefaultRecognition(
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

        // 运行扫描管线
        var scanPipeline = _pipelineFactory.CreateScanPipeline();
        var scanCtx = new ScanPipelineContext
        {
            ViewportImage = frame.Image,
            ViewportBoundsRaw = frame.ViewportBounds,
            ClientWidth = frame.ClientBounds.Width,
            GateTemplateThreshold = _settings!.RecognitionTuning.GateTemplateThreshold,
            // Floor state is exclusively controlled by the user's
            // manual floor switch.  Image-based floor classification
            // may remain available for diagnostics/tests, but it must
            // never participate in runtime map selection or alignment.
            SkipFloorDetection = true,
        };

        // 预构建地图指纹
        var maps = _mapRepo.GetMapsAsync().GetAwaiter().GetResult();
        var fingerprints = new List<object>();
        foreach (var mapObj in maps)
        {
            if (mapObj is MapRecord map)
            {
                map.NormalizeRecognition();
                var fp = BuildFingerprint(map);
                if (fp != null) fingerprints.Add(fp);
            }
        }
        scanCtx.FingerprintsRaw = fingerprints;

        scanCtx = (ScanPipelineContext)scanPipeline.RunAsync(scanCtx).GetAwaiter().GetResult();
        _lastScanPhaseTimings = scanCtx.PhaseTimings;

        _logCollector.Append(
            MapLogCategory.ScanLifecycle,
            scanCtx.IsFailed ? MapLogLevel.Warning : MapLogLevel.Info,
            $"扫描完成 · gates={scanCtx.DetectedGates.Count} · candidates={scanCtx.Candidates.Count} · "
            + $"selected={scanCtx.SelectedCandidate?.MapId ?? "<none>"} · "
            + $"failure={scanCtx.FailureReason ?? "<none>"}",
            elapsedMs: scanCtx.TotalWallMs,
            details: new()
            {
                ["gateCount"] = scanCtx.DetectedGates.Count,
                ["candidateCount"] = scanCtx.Candidates.Count,
                ["selectedMapId"] = scanCtx.SelectedCandidate?.MapId,
                ["failureReason"] = scanCtx.FailureReason,
                ["phaseTimings"] = scanCtx.PhaseTimings.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)pair.Value)
            });

        if (scanCtx.IsFailed || scanCtx.SelectedCandidate is null)
        {
            failureReason = $"识别失败：{scanCtx.FailureReason ?? "无匹配地图"}";
            return;
        }

        scanSucceeded = true;

        // 对齐引擎使用与扫描相同的有效门阈值
        var alignmentTuning = CreateInitialAlignmentRecognitionTuning();
        if (alignmentTuning.GateTemplateThreshold > GateTemplateRules.FallbackPairThreshold)
            alignmentTuning.GateTemplateThreshold = GateTemplateRules.FallbackPairThreshold;

        // ── 强制候选选择：对齐所有候选，让用户从中选择 ──
        if (alignmentTuning.ForceCandidateSelection
            && scanCtx.Candidates.Count >= 1)
        {
            var choiceList = new List<MapRecognitionChoice>();
            var topCandidates = scanCtx.Candidates.Take(
                Math.Min(scanCtx.Candidates.Count, 5));
            foreach (var candidate in topCandidates)
            {
                if (!Guid.TryParse(candidate.MapId, out var cMapId))
                    continue;
                try
                {
                    var candidateStructureTuning =
                        CreateInitialAlignmentStructureTuning();
                    MapRecognitionAttempt AlignCandidate() =>
                        MapCvAlignmentService.AlignSelectedCore(
                            _recognition, frame, cMapId,
                            session: null,
                            alignmentMode: _settings.OverlayAlignmentMode,
                            tuning: alignmentTuning,
                            structureTuning: candidateStructureTuning,
                            playerPrior: null, predictedViewportOrigin: null,
                            liveIgnoreRegions: null, candidateHistory: null,
                            alignmentSearchContext: null,
                            nativeScaleChangeRatio: 1.0,
                            mapClass: null,
                            route: SelectedAlignmentRoute.Default);
                    var candidateMap = _recognition.TryGetMap(cMapId);
                    MapFeatureCacheKey? candidateRepairKey = null;
                    var cAttempt = candidateMap is null
                        ? AlignCandidate()
                        : AlignUsingScaleCache(
                            frame,
                            candidateMap,
                            MapFloorRules.GetPrimaryFloorKey(candidateMap),
                            alignmentTuning,
                            candidateStructureTuning,
                            0d,
                            AlignCandidate,
                            out candidateRepairKey);
                    if (candidateRepairKey is not null)
                        repairCacheKeys[cMapId] = candidateRepairKey;
                    _lastDiagnostics = cAttempt.Diagnostics;
                    if (cAttempt.Recognition is { } cRec)
                    {
                        choiceList.Add(new MapRecognitionChoice
                        {
                            Recognition = cRec,
                            VectorError = 0d
                        });
                    }
                }
                catch { /* 单个候选对齐失败不影响其他候选 */ }
            }
            if (choiceList.Count > 0)
            {
                pendingChoices = choiceList;
                pendingChoicesReason =
                    "强制候选模式已开启，请选择正确地图。";
                return;
            }
            // 所有候选对齐失败：回退到标准路径，让 Top-1 尝试一次
        }

        // ── 标准路径：仅对齐选中的 Top-1 ──
        if (!Guid.TryParse(scanCtx.SelectedCandidate.MapId, out var mapId))
        {
            failureReason = $"识别失败：候选地图 ID 无效 ({scanCtx.SelectedCandidate.MapId})";
            return;
        }

        // Keep the scan-confirmed identity even when its first alignment
        // attempt fails. It is enough to render the persistent mini-map,
        // but deliberately carries no transform and is never used as a
        // full-map overlay result.
        var selectedMapForIdentity = _recognition.TryGetMap(mapId);
        if (selectedMapForIdentity is not null)
        {
            var floorKey = scanCtx.SelectedCandidate.FloorKey;
            if (MapFloorRules.GetFloorProfile(selectedMapForIdentity, floorKey)
                is null)
            {
                floorKey = MapFloorRules.GetPrimaryFloorKey(selectedMapForIdentity);
            }

            pendingSideEntranceIdentity = new RuntimeMapRecognition
            {
                Map = selectedMapForIdentity,
                FloorImagePath = _mapRepository.GetFloorOverlayPath(
                    selectedMapForIdentity,
                    floorKey),
                Result = new MapRecognitionResult
                {
                    MapId = selectedMapForIdentity.Id,
                    Floor = floorKey,
                    Confidence = scanCtx.SelectedCandidate.Score,
                    IdentityConfidence = scanCtx.SelectedCandidate.Score,
                    LocalizationConfidence = 0d,
                    Source = MapRecognitionSource.Automatic
                }
            };
        }

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"开始对齐 · mapId={mapId} · name={scanCtx.SelectedCandidate.MapDisplayName}");

        MapRecognitionAttempt attempt;
        try
        {
            var selectedStructureTuning =
                CreateInitialAlignmentStructureTuning();
            MapRecognitionAttempt AlignSelected() =>
                MapCvAlignmentService.AlignSelectedCore(
                    _recognition, frame, mapId,
                    session: null,
                    alignmentMode: _settings.OverlayAlignmentMode,
                    tuning: alignmentTuning,
                    structureTuning: selectedStructureTuning,
                    playerPrior: null, predictedViewportOrigin: null,
                    liveIgnoreRegions: null, candidateHistory: null,
                    alignmentSearchContext: null,
                    nativeScaleChangeRatio: 1.0,
                    mapClass: null,
                    route: SelectedAlignmentRoute.Default);
            var selectedMap = _recognition.TryGetMap(mapId);
            MapFeatureCacheKey? selectedRepairKey = null;
            attempt = selectedMap is null
                ? AlignSelected()
                : AlignUsingScaleCache(
                    frame,
                    selectedMap,
                    MapFloorRules.GetPrimaryFloorKey(selectedMap),
                    alignmentTuning,
                    selectedStructureTuning,
                    0d,
                    AlignSelected,
                    out selectedRepairKey);
            if (selectedRepairKey is not null)
                repairCacheKeys[mapId] = selectedRepairKey;
        }
        catch (Exception alignEx)
        {
            RecordResearchAttemptForMap(
                _recognition.TryGetMap(mapId), null, frame,
                new MapRecognitionAttempt { FailureReason = alignEx.Message },
                "initial-scan");
            failureReason = $"对齐异常：{alignEx.Message}";
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

        _lastDiagnostics = attempt.Diagnostics;

        RecordResearchAttemptForMap(
            attempt.Recognition?.Map ?? _recognition.TryGetMap(mapId),
            null, frame, attempt, "initial-scan");

        _logCollector.Append(
            MapLogCategory.Session,
            attempt.Recognition is null ? MapLogLevel.Warning : MapLogLevel.Info,
            $"对齐完成 · success={attempt.Recognition is not null} · "
            + $"reason={attempt.FailureReason ?? "<none>"}",
            details: new()
            {
                ["mapId"] = mapId,
                ["confidence"] = attempt.Recognition?.Result.Confidence,
                ["failureReason"] = attempt.FailureReason
            });

        if (attempt.Recognition is { } rec)
        {
            recognition = rec;
            _lastRecognition = rec;
            _statusMessage = $"对齐成功：{scanCtx.SelectedCandidate.MapDisplayName} · 置信度 {rec.Result.Confidence:P0}";
        }
        else if (attempt.Choices.Count > 0)
        {
            pendingChoices = attempt.Choices;
            pendingChoicesReason =
                attempt.FailureReason ?? string.Empty;
        }
        else
        {
            failureReason = $"对齐失败：{attempt.FailureReason}";
        }
    }
}
