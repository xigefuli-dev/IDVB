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
        var mapClass = _matchSession.Snapshot.MapClass;

        ScanPipelineContext scanCtx;
        var initialRecognition = MapOperationTraceAmbient.Current?.StartTopLevel(
            "initial_recognition",
            MapOperationWaitKind.Compute);
        try
        {
        // 运行扫描管线
        var scanPipeline = _pipelineFactory.CreateScanPipeline();
        scanCtx = new ScanPipelineContext
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
        using var repositoryRead = MapOperationTraceAmbient.StartChild(
            "map_repository_read",
            MapOperationWaitKind.Io);
        var maps = _mapRepo.GetMapsAsync().GetAwaiter().GetResult();
        repositoryRead.Complete();
        var fingerprints = new List<object>();
        using var fingerprintBuild = MapOperationTraceAmbient.StartChild(
            "fingerprint_build",
            MapOperationWaitKind.Compute);
        foreach (var mapObj in maps)
        {
            if (mapObj is MapRecord map
                && string.Equals(
                    map.Class,
                    mapClass,
                    StringComparison.OrdinalIgnoreCase))
            {
                using var mapFingerprint = MapOperationTraceAmbient.StartChild(
                    "map_fingerprint",
                    MapOperationWaitKind.Compute,
                    mapId: map.Id.ToString("D"),
                    floorKey: MapScanFloorRules.ResolveScanFloorKey(map));
                map.NormalizeRecognition();
                var fp = BuildFingerprint(map);
                if (fp != null) fingerprints.Add(fp);
            }
        }
        fingerprintBuild.Complete();
        scanCtx.FingerprintsRaw = fingerprints;

        using var scanPipelineSpan = MapOperationTraceAmbient.StartChild(
            "default_scan_pipeline",
            MapOperationWaitKind.Compute);
        scanCtx = (ScanPipelineContext)scanPipeline.RunAsync(scanCtx).GetAwaiter().GetResult();
        scanPipelineSpan.Complete();
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
        }
        finally
        {
            initialRecognition?.Complete();
        }

        // Keep the identity/candidate preparation time attached to the same
        // recognition phase. Candidate alignment starts only after this closes.
        var initialPostProcess = MapOperationTraceAmbient.StartTopLevel(
            "initial_recognition",
            MapOperationWaitKind.Compute);

        try
        {
        // 对齐引擎使用与扫描相同的有效门阈值
        var alignmentTuning = CreateInitialAlignmentRecognitionTuning();
        if (alignmentTuning.GateTemplateThreshold > GateTemplateRules.FallbackPairThreshold)
            alignmentTuning.GateTemplateThreshold = GateTemplateRules.FallbackPairThreshold;

        // ── 强制候选选择：对齐所有候选，让用户从中选择 ──
        if ((alignmentTuning.ForceCandidateSelection
                || _settings.CandidateDecisionMode
                    != MapCandidateDecisionMode.Traditional)
            && scanCtx.Candidates.Count >= 1)
        {
            // 后台扫描仅识别不对齐：跳过逐候选对齐，直接把候选包装为
            // 无变换身份选择项，等玩家开图时确认并执行一次对齐。
            if (recognizeOnly)
            {
                pendingChoices = BackgroundScanRules.BuildBackgroundCandidateChoices(
                    scanCtx.Candidates,
                    maxCandidates: 5,
                    mapId => _recognition.TryGetMap(mapId),
                    (map, floorKey, score) =>
                        BackgroundScanRules.BuildIdentityOnlyRecognition(
                            map,
                            floorKey,
                            score,
                            _mapRepository.GetFloorOverlayPath),
                    out failureReason);
                if (pendingChoices is not null)
                {
                    pendingChoicesReason =
                        "后台扫描候选：请打开游戏地图后确认。";
                    initialPostProcess.Complete();
                    return;
                }
                // 无可用候选：跳过逐候选对齐（后台扫描仅识别不对齐），
                // 回退到标准路径让 Top-1 尝试构建身份。
            }
            else
            {
                initialPostProcess.Complete();
                var choiceList = new List<MapRecognitionChoice>();
                var topCandidates = scanCtx.Candidates.Take(
                    Math.Min(scanCtx.Candidates.Count, 5));
                foreach (var candidate in topCandidates)
                {
                    if (!Guid.TryParse(candidate.MapId, out var cMapId))
                        continue;
                    var candidateAlignment = MapOperationTraceAmbient.StartTopLevel(
                        "selected_candidate_alignment",
                        MapOperationWaitKind.Compute,
                        mapId: candidate.MapId,
                        floorKey: candidate.FloorKey,
                        attemptIndex: candidate.Rank);
                    try
                    {
                        var candidateMap = _recognition.TryGetMap(cMapId);
                        var candidateFloorKey = candidate.FloorKey;
                        var candidateStructureTuning = candidateMap is null
                            ? CreateInitialAlignmentStructureTuning()
                            : CreateStructureTuningForFloor(
                                candidateMap,
                                candidateFloorKey,
                                CreateInitialAlignmentStructureTuning());
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
                                mapClass: mapClass,
                                route: SelectedAlignmentRoute.Default);
                        MapFeatureCacheKey? candidateRepairKey = null;
                        var cAttempt = candidateMap is null
                            ? AlignCandidate()
                            : AlignUsingScaleCache(
                                frame,
                                candidateMap,
                                candidateFloorKey,
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
                    finally
                    {
                        candidateAlignment.Complete();
                    }
                }
                initialPostProcess.Complete();
                if (choiceList.Count > 0)
                {
                    pendingChoices = choiceList;
                    pendingChoicesReason =
                        "强制候选模式已开启，请选择正确地图。";
                    return;
                }
                // 所有候选对齐失败：回退到标准路径，让 Top-1 尝试一次
            }
        }

        // ── 标准路径：仅对齐选中的 Top-1 ──
        if (!Guid.TryParse(scanCtx.SelectedCandidate.MapId, out var mapId))
        {
            failureReason = $"识别失败：候选地图 ID 无效 ({scanCtx.SelectedCandidate.MapId})";
            initialPostProcess.Complete();
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
                floorKey = MapScanFloorRules.ResolveScanFloorKey(selectedMapForIdentity);
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

        // 后台扫描仅识别不对齐：保留无变换身份，由玩家开图时消费对齐。
        if (recognizeOnly)
        {
            if (pendingSideEntranceIdentity is { } identity)
            {
                recognition = identity;
                _statusMessage =
                    $"后台扫描已识别地图：{scanCtx.SelectedCandidate.MapDisplayName}（打开游戏地图后对齐）";
                initialPostProcess.Complete();
                return;
            }

            failureReason = "识别失败：无法加载所选地图身份。";
            initialPostProcess.Complete();
            return;
        }

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"开始对齐 · mapId={mapId} · name={scanCtx.SelectedCandidate.MapDisplayName}");

        initialPostProcess.Complete();
        MapRecognitionAttempt attempt;
        var selectedAlignment = MapOperationTraceAmbient.StartTopLevel(
            "selected_candidate_alignment",
            MapOperationWaitKind.Compute,
            mapId: mapId.ToString("D"),
            floorKey: scanCtx.SelectedCandidate.FloorKey,
            attemptIndex: 0);
        try
        {
            var selectedMap = _recognition.TryGetMap(mapId);
            var selectedFloorKey = scanCtx.SelectedCandidate.FloorKey;
            var selectedStructureTuning = selectedMap is null
                ? CreateInitialAlignmentStructureTuning()
                : CreateStructureTuningForFloor(
                    selectedMap,
                    selectedFloorKey,
                    CreateInitialAlignmentStructureTuning());
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
                    mapClass: mapClass,
                    route: SelectedAlignmentRoute.Default);
            MapFeatureCacheKey? selectedRepairKey = null;
            attempt = selectedMap is null
                ? AlignSelected()
                : AlignUsingScaleCache(
                    frame,
                    selectedMap,
                    selectedFloorKey,
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
        finally
        {
            selectedAlignment.Complete();
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
            _mapLease.Bind(_matchSession.Snapshot, rec.Map.Id);
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
        finally
        {
            initialPostProcess.Complete();
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.Pipeline.InitialRecognition.Default。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
