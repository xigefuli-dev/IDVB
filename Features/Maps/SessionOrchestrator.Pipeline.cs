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
            RuntimeMapRecognition? aligned = null;
            string? failureReason = null;
            MapFeatureCacheKey? repairCacheKey = null; MapRecognitionAttempt? finalAttempt = null;
            await Task.Run(() =>
            {
                using var ambientDeadline = noDoorDeadline?.EnterAmbient();
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
                    if (alignmentSession.SideEntranceScanPriorConfidence > 0d)
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
                    var scaleSeed = primarySession?.LockedTransform
                        ?? alignmentSession.LockedTransform;
                    scaleSeed = CreateCrossFloorScaleSeed(
                        locked.Map,
                        primaryFloorKey,
                        targetFloorKey,
                        scaleSeed);
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
