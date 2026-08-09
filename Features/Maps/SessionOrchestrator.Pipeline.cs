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
    // ════════════════ Map Open Alignment（仅对齐，不扫描）════════════════

    private async Task RunMapOpenAlignmentCoreAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var alignmentWallClock = Stopwatch.StartNew();
        var recoveringSelectedIdentity = _lastRecognition is null;
        var locked = _lastRecognition ?? _pendingAlignmentIdentity;
        if (locked is null)
        {
            _statusMessage = "尚未锁定地图，请先按快捷扫描键确认地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _statusMessage = "地图已重新打开，正在重新对齐……";
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var targetFloorKey = _currentFloorKey ?? primaryFloorKey;
        var isOtherFloor = !string.Equals(
            targetFloorKey,
            primaryFloorKey,
            StringComparison.Ordinal);
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"开始仅对齐 · map={locked.Map.Id} · floor={targetFloorKey} "
            + $"· route={(isOtherFloor ? "structure-only-floor" : "primary-floor")} "
            + $"· toggleVersion={toggle.Version}");

        // 开图动画等待（在调用线程即可，不阻塞）
        await Task.Delay(
            _settings!.SessionTuning.OpeningAnimationDelayMilliseconds,
            cancellationToken);

        // Do not align against the first animation frame after the map opens.
        // That frame can have a different crop/scale from the settled map.
        var frame = await CaptureStableViewportAsync("仅对齐");
        cancellationToken.ThrowIfCancellationRequested();
        if (frame is null)
        {
            _statusMessage = string.IsNullOrWhiteSpace(_lastStableCaptureFailureReason)
                ? "地图截图失败。"
                : _lastStableCaptureFailureReason;
            _lastAlignmentPhaseTimings = new Dictionary<string, double>
            {
                ["wall_clock"] = alignmentWallClock.Elapsed.TotalMilliseconds
            };
            _logCollector.Append(
                MapLogCategory.ViewportCapture,
                MapLogLevel.Warning,
                _statusMessage,
                elapsedMs: alignmentWallClock.Elapsed.TotalMilliseconds);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alignmentMode = _settings.OverlayAlignmentMode;
            var structureTuning = recoveringSelectedIdentity
                ? CreateInitialAlignmentStructureTuning()
                : CreateEffectiveStructureTuning();
            var tuning = recoveringSelectedIdentity
                ? CreateInitialAlignmentRecognitionTuning()
                : _settings.RecognitionTuning.Clone();
            if (tuning.GateTemplateThreshold > GateTemplateRules.FallbackPairThreshold)
                tuning.GateTemplateThreshold = GateTemplateRules.FallbackPairThreshold;

            RuntimeMapRecognition? aligned = null;
            string? failureReason = null;
            MapFeatureCacheKey? repairCacheKey = null;

            await Task.Run(() =>
            {
                // 复用持久化的对齐会话以保留侧门身份先验与门对锁定状态。若持久化
                // 会话与当前锁定地图不一致（如手动识别或换图），则从结果重建。
                var session = recoveringSelectedIdentity
                        && _pendingAlignmentSeed is { } pendingSeed
                    ? pendingSeed
                    : _lastAlignmentSession is { } lastSession
                        && lastSession.MapId == locked.Map.Id
                        && lastSession.MapUpdatedAt == locked.Map.UpdatedAt
                    ? lastSession
                    : MapAlignmentSession.FromRecognition(
                        locked.Map,
                        locked.Result);

                // Secondary floors are locked to the map identity already, so
                // they must use their own static structure directly.  This
                // branch intentionally runs before the primary-floor side-door
                // route and never invokes gate detection.
                var primarySession = _primaryFloorAlignmentSession is { } savedPrimary
                        && savedPrimary.MapId == locked.Map.Id
                        && savedPrimary.MapUpdatedAt == locked.Map.UpdatedAt
                    ? savedPrimary
                    : null;
                var alignmentSession = isOtherFloor
                    ? session
                    : primarySession ?? session;

                MapRecognitionAttempt RunFallback()
                {
                    if (isOtherFloor)
                    {
                        var scaleSeed = primarySession?.LockedTransform
                            ?? alignmentSession.LockedTransform;
                        scaleSeed = CreateCrossFloorScaleSeed(
                            locked.Map, primaryFloorKey, targetFloorKey, scaleSeed);
                        return AlignExactManualFloor(
                            frame,
                            locked,
                            targetFloorKey,
                            scaleSeed,
                            alignmentMode,
                            tuning,
                            structureTuning,
                            alignmentSession.SideEntranceScanPriorConfidence);
                    }
                    if (recoveringSelectedIdentity)
                    {
                        return _recognition.AlignSideEntrance(
                            frame,
                            locked.Map.Id,
                            alignmentSession,
                            alignmentMode,
                            tuning,
                            structureTuning,
                            alignmentSearchContext:
                                CreateSideEntranceSearchContext(
                                    alignmentSession,
                                    tuning,
                                    useInitialHighPrecisionRecovery: true));
                    }
                    if (alignmentSession.SideEntranceScanPriorConfidence > 0d)
                    {
                        return AlignLockedSideEntranceFloor(
                            frame,
                            locked,
                            alignmentSession,
                            alignmentMode,
                            tuning,
                            structureTuning);
                    }
                    return MapCvAlignmentService.AlignSelectedCore(
                            _recognition,
                            frame,
                            locked.Map.Id,
                            session: alignmentSession,
                            alignmentMode: alignmentMode,
                            tuning: tuning,
                            structureTuning: structureTuning,
                            playerPrior: null,
                            predictedViewportOrigin: null,
                            liveIgnoreRegions: null,
                            candidateHistory: null,
                            alignmentSearchContext: null,
                            nativeScaleChangeRatio: 1.0,
                            mapClass: null,
                            route: SelectedAlignmentRoute.Default);
                }

                var attempt = AlignUsingScaleCache(
                    frame,
                    locked.Map,
                    targetFloorKey,
                    tuning,
                    structureTuning,
                    alignmentSession.SideEntranceScanPriorConfidence,
                    RunFallback,
                    out var localRepairKey);
                repairCacheKey = localRepairKey;
                _lastDiagnostics = attempt.Diagnostics;
                aligned = attempt.Recognition;
                failureReason = attempt.FailureReason;
            });
            cancellationToken.ThrowIfCancellationRequested();
            _lastAlignmentPhaseTimings = BuildAlignmentPhaseTimings(
                _lastDiagnostics,
                alignmentWallClock.Elapsed.TotalMilliseconds);

            // 过期协程防护：后台对齐期间玩家已关闭或重新开关地图，则丢弃结果
            if (!IsCurrentMatchOperation(operationMatch)
                || !_gameMapToggleState.IsCurrent(toggle))
            {
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    "仅对齐结果已丢弃（地图已关闭或重新打开）。");
                return;
            }

            if (aligned is not null)
            {
                await RepairMapCacheAsync(repairCacheKey, aligned, frame);
                await PersistPreprocessedScaleAsync(
                    aligned,
                    frame,
                    _lastDiagnostics);
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                RecordSuccessfulAlignment(aligned, frame);
                _lastRecognition = aligned;
                _pendingAlignmentIdentity = null;
                _pendingAlignmentSeed = null;
                var updatedSession = UpdateAlignmentSession(
                    _lastAlignmentSession,
                    aligned);
                _lastAlignmentSession = updatedSession;
                RememberPrimaryFloorSession(aligned, updatedSession);
                _lastGameBounds = frame.ClientBounds;
                _lastGameWindowHandle = frame.WindowHandle;
                _statusMessage =
                    $"地图已对齐：{aligned.Map.DisplayName} · {aligned.Result.Floor.ToUpperInvariant()}";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"仅对齐完成 · map={aligned.Map.Id} · floor={aligned.Result.Floor}",
                    details: new()
                    {
                        ["mapId"] = aligned.Map.Id,
                        ["floor"] = aligned.Result.Floor,
                        ["confidence"] = aligned.Result.Confidence
                    });
                _overlay.UpdateMap(
                    aligned,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _settings.ShowOverlayStatus);
                ShowTransientAlignmentSuccess(
                    aligned,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _lastDiagnostics);
                _overlay.Show();
                RefreshMiniMapForCurrentFloor();
            }
            else
            {
                // 对齐失败时不能把扫描时的旧变换重新渲染成“对齐成功”。
                // 原先这里直接 UpdateMap(locked)，用户看到的就是每次只隐藏/显示，
                // 实际上没有任何新的对齐结果。
                var manualFloorLabel = MapFloorRules.GetFloorDisplayName(
                    locked.Map,
                    targetFloorKey);
                _statusMessage = recoveringSelectedIdentity
                    ? $"所选地图暂未完成首次对齐：{locked.Map.DisplayName} · "
                        + $"{failureReason ?? "无法匹配当前画面"}"
                    : $"对齐未更新：当前按{manualFloorLabel}对齐；"
                        + $"{failureReason ?? "无法匹配当前画面"}";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Warning,
                    $"仅对齐未更新 · map={locked.Map.Id} · reason={failureReason ?? "<none>"}");
                _overlay.ClearMap();
                ShowTransientOverlayStatus(
                    MapOverlayStatusLevel.Failure,
                    "地图重新对齐失败",
                    _statusMessage,
                    recoveringSelectedIdentity
                        ? "已保留所选地图身份；下次重新打开地图会继续高精度重试。"
                        : "本次未复用旧变换；请确认 IDVB 手动楼层是否正确，再重新打开地图。",
                    frame.ClientBounds,
                    frame.WindowHandle);
                _overlay.Show();
                RefreshMiniMapForCurrentFloor();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _statusMessage = $"仅对齐异常：{ex.Message}";
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Error,
                _statusMessage,
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
            ShowTransientOverlayStatus(
                MapOverlayStatusLevel.Failure,
                "地图重新对齐失败",
                _statusMessage,
                "对齐执行异常；请重新打开地图重试。",
                frame.ClientBounds,
                frame.WindowHandle);
            _overlay.Show();
        }
        finally
        {
            frame.Dispose();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunRecognitionPipelineCoreAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var scanWallClock = Stopwatch.StartNew();
        // 开图动画等待（在调用线程即可，不阻塞）
        await Task.Delay(
            _settings!.SessionTuning.OpeningAnimationDelayMilliseconds,
            cancellationToken);

        // 捕获稳定帧
        if (!_captureSvc.TryCaptureViewport(
                ResolveMapViewportForCurrentWindow(),
                out var frameObj,
                out var captureFailureReason)
            || frameObj is not CapturedGameFrame frame)
        {
            ReportScanCaptureFailure(captureFailureReason, scanWallClock);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryAutoMatchResolutionPresetAsync(frame.ClientBounds);
            RuntimeMapRecognition? recognition = null;
            string? failureReason = null;
            IReadOnlyList<MapRecognitionChoice>? pendingChoices = null;
            string pendingChoicesReason = string.Empty;
            // 侧门扫描的种子会话（含 SideEntranceScanPriorConfidence），
            // 在 Task.Run 内部捕获，供外部候选确认路径使用。
            MapAlignmentSession? pendingSideEntranceSeed = null;
            RuntimeMapRecognition? pendingSideEntranceIdentity = null;
            SideEntranceScanResult? pendingSideEntranceScan = null;
            var repairCacheKeys = new Dictionary<Guid, MapFeatureCacheKey>();
            var scanSucceeded = false;

            await Task.Run(() =>
            {
                if (_settings!.FirstScanStrategy == FirstScanStrategy.SideEntrance)
                {
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
                            _settings.RecognitionTuning,
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

                // 运行扫描管线
                var scanPipeline = _pipelineFactory.CreateScanPipeline();
                var scanCtx = new ScanPipelineContext
                {
                    ViewportImage = frame.Image,
                    ViewportBoundsRaw = frame.ViewportBounds,
                    ClientWidth = frame.ClientBounds.Width,
                    GateTemplateThreshold = _settings.RecognitionTuning.GateTemplateThreshold,
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
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            if (recognition is null
                && pendingSideEntranceIdentity is not null
                && pendingSideEntranceSeed is not null)
            {
                _pendingAlignmentIdentity = pendingSideEntranceIdentity;
                _pendingAlignmentSeed = pendingSideEntranceSeed;
            }

            if (scanSucceeded)
            {
                _gameMapToggleState.MarkOpen();
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    "扫描成功，已同步原生地图为打开状态；下一次地图键将执行关闭，随后重新打开时执行对齐。");
            }

            // ── 候选地图选择：当识别有歧义或 ForceCandidateSelection 开启时 ──
            if (pendingChoices is { Count: > 0 } && recognition is null)
            {
                recognition = await ResolveCandidateSelectionAsync(
                    frame,
                    pendingChoices,
                    pendingChoicesReason,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
            }

            // A side-entrance candidate is only scan evidence. Once the user
            // (or headless policy) confirms it, run exactly one selected-map
            // alignment using the mandatory scanned gate as the provisional
            // seed. Never render the provisional scan transform.
            if (pendingSideEntranceScan is { Gate: { } selectedSideGate }
                && recognition is not null
                && pendingSideEntranceSeed is null)
            {
                var selectedCandidate = pendingSideEntranceScan.Candidates
                    .FirstOrDefault(candidate => candidate.Map.Id == recognition.Map.Id);
                if (selectedCandidate is null)
                {
                    recognition = null;
                    failureReason =
                        "侧门候选已选择，但候选不属于本次扫描结果，未提交地图锁定。";
                }
                else if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                             selectedCandidate,
                             selectedSideGate,
                             frame.ViewportBounds,
                             out var selectedSeed,
                             out var selectedSeedFailure))
                {
                    recognition = null;
                    failureReason = $"侧门扫描种子无效：{selectedSeedFailure}";
                }
                else
                {
                    pendingSideEntranceSeed = selectedSeed;
                    _pendingAlignmentIdentity = recognition;
                    _pendingAlignmentSeed = selectedSeed;
                    var selectedTuning = CreateInitialAlignmentRecognitionTuning();
                    if (selectedTuning.GateTemplateThreshold
                        > GateTemplateRules.FallbackPairThreshold)
                    {
                        selectedTuning.GateTemplateThreshold =
                            GateTemplateRules.FallbackPairThreshold;
                    }

                    var selectedSearchContext =
                        CreateSideEntranceSearchContext(
                            selectedSeed,
                            selectedTuning,
                            useInitialHighPrecisionRecovery: true);
                    var selectedStructureTuning =
                        CreateInitialAlignmentStructureTuning();
                    MapFeatureCacheKey? selectedRepairKey = null;
                    var selectedAttempt = await Task.Run(() =>
                    {
                        MapRecognitionAttempt AlignSelectedSide() =>
                            _recognition.AlignSideEntrance(
                                frame,
                                selectedSeed.MapId,
                                selectedSeed,
                                _settings.OverlayAlignmentMode,
                                selectedTuning,
                                selectedStructureTuning,
                                alignmentSearchContext: selectedSearchContext);
                        return AlignUsingScaleCache(
                            frame,
                            selectedCandidate.Map,
                            selectedSeed.FloorKey,
                            selectedTuning,
                            selectedStructureTuning,
                            selectedSeed.SideEntranceScanPriorConfidence,
                            AlignSelectedSide,
                            out selectedRepairKey);
                    });
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch))
                        return;
                    if (selectedRepairKey is not null)
                        repairCacheKeys[selectedSeed.MapId] = selectedRepairKey;
                    _lastDiagnostics = selectedAttempt.Diagnostics;
                    if (selectedAttempt.Recognition is { } selectedRecognition)
                    {
                        recognition = selectedRecognition;
                        _statusMessage =
                            $"侧门地图已确认并完成对齐：{recognition.Map.DisplayName} · "
                            + $"置信度 {recognition.Result.Confidence:P0}";
                    }
                    else
                    {
                        recognition = null;
                        var selectedFailure = string.IsNullOrWhiteSpace(
                            selectedAttempt.StructureFailureReason)
                            ? selectedAttempt.FailureReason
                            : selectedAttempt.StructureFailureReason;
                        failureReason =
                            $"侧门地图已选择，但首次对齐失败：{selectedFailure}；"
                            + "已保留所选地图身份，下次重新打开地图将按高精度策略重试。";
                    }
                }
            }

            if (recognition is { } identityLock)
            {
                var manualFloorAlignment =
                    await AlignQuickScanManualFloorAsync(frame, identityLock);
                recognition = manualFloorAlignment.Recognition;
                failureReason ??= manualFloorAlignment.FailureReason;
                _lastDiagnostics =
                    manualFloorAlignment.Diagnostics ?? _lastDiagnostics;
            }

            if (recognition is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                _hasCompletedQuickScanAlignment = true;
                repairCacheKeys.TryGetValue(
                    recognition.Map.Id,
                    out var repairCacheKey);
                await RepairMapCacheAsync(repairCacheKey, recognition, frame);
                await PersistPreprocessedScaleAsync(
                    recognition,
                    frame,
                    _lastDiagnostics);
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                RecordSuccessfulAlignment(recognition, frame);
                _lastRecognition = recognition;
                _pendingAlignmentIdentity = null;
                _pendingAlignmentSeed = null;
                // 侧门扫描时 _lastAlignmentSession 可能尚未初始化；
                // 此时回退到 Task.Run 内部捕获的 pendingSideEntranceSeed，
                // 以保留 SideEntranceScanPriorConfidence。
                _lastAlignmentSession = UpdateAlignmentSession(
                    _lastAlignmentSession ?? pendingSideEntranceSeed,
                    recognition);
                RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
                _lastGameBounds = frame.ClientBounds;
                _lastGameWindowHandle = frame.WindowHandle;

                _logCollector.Append(
                    MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"开始更新 Overlay · map={recognition.Map.Id} · floor={recognition.Result.Floor} · "
                    + $"imageExists={File.Exists(recognition.FloorImagePath)} · visibleBefore={_overlay.IsVisible}",
                    details: new()
                    {
                        ["floorImagePath"] = recognition.FloorImagePath,
                        ["windowHandle"] = $"0x{frame.WindowHandle.ToInt64():X}",
                        ["gameBounds"] = $"{frame.ClientBounds.X:F0},{frame.ClientBounds.Y:F0},{frame.ClientBounds.Width:F0}x{frame.ClientBounds.Height:F0}",
                        ["hasTransform"] = recognition.Result.OverlayTransform is not null
                    });
                _overlay.UpdateMap(
                    recognition,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _settings.ShowOverlayStatus);
                ShowTransientAlignmentSuccess(
                    recognition,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _lastDiagnostics);
                _overlay.Show();
                _logCollector.Append(
                    MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"Overlay 更新完成 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
                // 识别成功后刷新持久小地图（若启用）
                RefreshMiniMapForCurrentFloor();
            }
            else if (failureReason is not null)
            {
                _statusMessage = failureReason;
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    _statusMessage);
                ShowTransientOverlayStatus(
                    MapOverlayStatusLevel.Failure,
                    "Overlay 识别失败",
                    failureReason,
                    "请查看日志中的扫描、对齐和 Overlay 记录。",
                    frame.ClientBounds,
                    frame.WindowHandle);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _statusMessage = $"识别异常：{ex.Message}";
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Error,
                _statusMessage,
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
            ShowTransientOverlayStatus(
                MapOverlayStatusLevel.Failure,
                "Overlay 识别异常",
                _statusMessage,
                "请查看日志中的异常堆栈。",
                frame.ClientBounds,
                frame.WindowHandle);
        }
        finally
        {
            frame.Dispose();
        }
    }

}
