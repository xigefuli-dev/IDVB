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
        var frame = await CaptureStableViewportAsync(
            "仅对齐",
            cancellationToken);
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
            _overlay.ClearMap();
            if (_lastGameBounds.IsValid && _lastGameWindowHandle != IntPtr.Zero)
            {
                ShowTransientOverlayStatus(
                    MapOverlayStatusLevel.Failure,
                    "地图重新对齐失败",
                    _statusMessage,
                    "请保持游戏完整地图打开且画面稳定，然后重新打开地图重试。",
                    _lastGameBounds,
                    _lastGameWindowHandle);
                _overlay.Show();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Start the alignment budget after stable-frame input preparation.
        using var noDoorDeadline = new NoDoorAlignmentDeadline(
            cancellationToken,
            _settings.StructureRegistrationTuning
                .StructureFallbackBudgetMilliseconds);

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
            var adaptiveKey = CreateAdaptiveScaleKey(
                frame,
                locked.Map,
                targetFloorKey);
            RuntimeMapRecognition? aligned = null;
            string? failureReason = null;
            MapFeatureCacheKey? repairCacheKey = null; MapRecognitionAttempt? finalAttempt = null;
            await Task.Run(() =>
            {
                using var ambientDeadline = noDoorDeadline?.EnterAmbient();
                // 复用持久化的对齐会话以保留侧门身份先验与门对锁定状态。若持久化
                // 会话与当前锁定地图不一致（如手动识别或换图），则从结果重建。
                var lastSession = _lastAlignmentSession;
                var canReuseLastSession = lastSession is not null
                    && lastSession.MapId == locked.Map.Id
                    && lastSession.MapUpdatedAt == locked.Map.UpdatedAt
                    && (!IsAdaptiveScaleEnabled
                        || _lastReliableAdaptiveKey == adaptiveKey)
                    && CanUseAdaptiveReliableSession(lastSession, adaptiveKey);
                var session = MapOpenAlignmentRouteRules
                    .ResolveMapOpenAlignmentSession(
                    locked.Map,
                    locked.Result,
                    recoveringSelectedIdentity
                        ? _pendingAlignmentSeed
                        : null,
                    lastSession,
                    canReuseLastSession);

                // Secondary floors are locked to the map identity already, so
                // they must use their own static structure directly.  This
                // branch intentionally runs before the primary-floor side-door
                // route and never invokes gate detection.
                var primarySession = _primaryFloorAlignmentSession is { } savedPrimary
                        && savedPrimary.MapId == locked.Map.Id
                        && savedPrimary.MapUpdatedAt == locked.Map.UpdatedAt
                        && (!IsAdaptiveScaleEnabled
                            || _primaryFloorAdaptiveKey == adaptiveKey)
                        && CanUseAdaptiveReliableSession(savedPrimary, adaptiveKey)
                    ? savedPrimary
                    : null;
                var alignmentSession = isOtherFloor
                    ? session
                    : primarySession ?? session;

                MapRecognitionAttempt RunFallback(bool tryDirectSideFeature)
                {
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
                    // The configured first-scan strategy owns the alignment
                    // route for the entire match. Session evidence selects a
                    // seed within that route; it must never switch a side-door
                    // match into the default dual-gate pipeline.
                    if (MapOpenAlignmentRouteRules.ResolveMatchRoute(
                            _settings!.FirstScanStrategy,
                            alignmentSession)
                        == SelectedAlignmentRoute.SideEntrance)
                    {
                        return AlignLockedSideEntranceFloor(
                            frame,
                            locked,
                            alignmentSession,
                            alignmentMode,
                            tuning,
                            structureTuning,
                            tryDirectFeature: tryDirectSideFeature);
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

                MapRecognitionAttempt attempt;
                MapFeatureCacheKey? localRepairKey;
                if (isOtherFloor)
                {
                    var scaleSeed = MapFloorScaleSeedRules
                        .CreateIndependentFloorSeed(locked.Map, targetFloorKey);
                    attempt = AlignExactManualFloor(
                        frame,
                        locked,
                        targetFloorKey,
                        scaleSeed,
                        alignmentMode,
                        tuning,
                        structureTuning,
                        alignmentSession.SideEntranceScanPriorConfidence,
                        out localRepairKey);
                }
                else
                {
                    // 热启动快速路径：同一张图连续开图直接复用上次锁定变换。
                    // 复用 AlignExactManualFloor 的 same-floor-local 同款调用
                    // （TryGetReliableFloorAlignment + AlignNoDoorLocalStructure），
                    // 在锁定变换附近做局部平移搜索、固定 scale——吸收细微位移，
                    // 是正常识别结果（不标记 ReusedLastTransform，reliable 状态可刷新）。
                    // 命中则短路发布，跳过完整管线。
                    MapRecognitionAttempt? quickAttempt = null;
                    if (!recoveringSelectedIdentity)
                    {
                        var reliableCheck = TryGetReliableFloorAlignment(
                            operationMatch,
                            frame,
                            locked.Map,
                            targetFloorKey);
                        if (reliableCheck is not null)
                        {
                            var candidate = AlignNoDoorLocalStructure(
                                frame,
                                locked,
                                targetFloorKey,
                                reliableCheck.Session,
                                alignmentMode,
                                tuning,
                                structureTuning,
                                reliableCheck.CandidateHistory,
                                alignmentSession.SideEntranceScanPriorConfidence);
                            LogNoDoorStage(
                                "hot-start-local",
                                candidate.Recognition is not null,
                                candidate,
                                candidate.Diagnostics.TotalMilliseconds,
                                new Dictionary<string, object?>
                                {
                                    ["historyCount"] =
                                        reliableCheck.CandidateHistory.Count,
                                    ["scale"] =
                                        reliableCheck.Session.LockedTransform.ScaleX
                                });
                            // 热启动短路质量门槛：structure-only 固定 scale 验证的
                            // 置信度可能因单向指标缺陷而偏低（如 0.56~0.62）。只有达到
                            // 可靠定位样本水平才短路，否则继续走稳健的双门/侧门路径，
                            // 避免用低质量结果覆盖本可给出高置信的门路径（根因⑥）。
                            if (candidate.Recognition is { } localRecognition
                                && MapFeatureCacheRules.IsReliableLocalizationSample(
                                    localRecognition.Result,
                                    _settings!.SessionTuning.HighConfidence,
                                    _settings.StructureRegistrationTuning.MinimumCandidateMargin))
                            {
                                quickAttempt = candidate;
                            }
                        }
                    }

                    if (quickAttempt is not null)
                    {
                        attempt = quickAttempt;
                        localRepairKey = null;
                    }
                    else
                    {
                        attempt = AlignMapOpenWithPreferredRoute(
                            frame,
                            locked,
                            targetFloorKey,
                            isOtherFloor,
                            recoveringSelectedIdentity,
                            alignmentSession,
                            alignmentMode,
                            tuning,
                            structureTuning,
                            RunFallback,
                            out localRepairKey);
                    }
                }
                repairCacheKey = localRepairKey;
                finalAttempt = attempt;
                _lastDiagnostics = attempt.Diagnostics;
                aligned = attempt.Recognition;
                failureReason = attempt.FailureReason;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (finalAttempt is { } mapOpenAttempt)
                RecordResearchAttempt(
                    locked.Map, targetFloorKey, frame, mapOpenAttempt,
                    isOtherFloor ? "floor-switch" : "map-open",
                    isOtherFloor ? locked.Result.OverlayTransform : null);
            LogMapOpenAlignmentTimings(
                locked,
                targetFloorKey,
                isOtherFloor,
                aligned is not null,
                failureReason,
                alignmentWallClock.Elapsed.TotalMilliseconds);

            if (!await PublishMapOpenAlignmentResultAsync(
                    toggle,
                    operationMatch,
                    frame,
                    locked,
                    targetFloorKey,
                    recoveringSelectedIdentity,
                    aligned,
                    failureReason,
                    repairCacheKey))
            {
                return;
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

}
