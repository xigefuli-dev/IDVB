using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using IDVBuff.Pipeline;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// 当开启「通过标签选择地图」时，快捷扫描跳过原本的录制截图与批量比对，
    /// 直接获取当前地图类的全部地图，并根据后台扫描设置进入后台待消费或前台候选选择。
    /// </summary>
    private async Task RunTagBasedScanAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var mapClass = operationMatch.MapClass;
        if (string.IsNullOrWhiteSpace(mapClass))
        {
            _statusMessage = "请先在对局控件中选择地图类。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _scanProgressOverlay.Report(0.15d, "正在读取地图库...");
        var allMaps = await _mapRepository.GetMapsAsync();
        var classMaps = allMaps
            .Where(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ThenBy(map => map.Id)
            .ToList();

        if (classMaps.Count == 0)
        {
            _statusMessage = $"地图类「{mapClass}」下未找到已记录地图。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var choices = classMaps.Select(map =>
        {
            var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            return new MapRecognitionChoice
            {
                Recognition = new RuntimeMapRecognition
                {
                    Map = map,
                    FloorImagePath = _mapRepository.GetFloorOverlayPath(map, floorKey),
                    Result = new MapRecognitionResult
                    {
                        MapId = map.Id,
                        Floor = floorKey,
                        Confidence = 0d,
                        IdentityConfidence = 0d,
                        LocalizationConfidence = 0d,
                        Source = MapRecognitionSource.Automatic
                    }
                },
                IsReferenceOnly = true,
                EvidenceLabel = "手动标签筛选",
                PreferredOrder = int.MaxValue
            };
        }).ToList();

        CapturedGameFrame? frame = null;
        var ownsFrame = false;
        try
        {
            if (_captureSvc.TryCaptureClient(out var frameObj, out _)
                && frameObj is CapturedGameFrame captured)
            {
                frame = captured;
                ownsFrame = true;
            }
            else
            {
                var blank = new Mat(600, 800, MatType.CV_8UC3, Scalar.Black);
                var bounds = new MapScreenRect(0, 0, 800, 600);
                frame = new CapturedGameFrame(blank, bounds, bounds, IntPtr.Zero);
                ownsFrame = true;
            }

            var backgroundMode = _settings!.BackgroundScanEnabled;
            if (backgroundMode)
            {
                _scanProgressOverlay.Report(0.85d, "正在准备后台候选...");
                _pendingBackgroundIdentity = null;
                _pendingBackgroundChoices = choices;
                _pendingBackgroundChoicesReason = "通过标签选择地图";
                _pendingBackgroundLearningResult = null;
                _pendingBackgroundChoicesAreDisplayReady = true;

                if (!_headless && _activeCandidateSelector is null)
                {
                    _pendingBackgroundChoicePreviews =
                        await MapManualCandidateWindow.PrepareChoicePreviewsAsync(
                            choices,
                            _mapRepository);
                    _pendingBackgroundCandidateFrame = new CapturedGameFrame(
                        frame.Image.Clone(),
                        frame.ClientBounds,
                        frame.ViewportBounds,
                        frame.WindowHandle);
                    _pendingBackgroundLivePreview =
                        await MapManualCandidateWindow.PrepareLivePreviewAsync(
                            _pendingBackgroundCandidateFrame,
                            choices,
                            _pendingBackgroundCandidateFrame.ViewportBounds);
                }

                _backgroundScanStatus = BackgroundScanStatus.CompletedAmbiguous;
                _statusMessage = $"后台扫描完成：{choices.Count} 个候选地图待确认（打开游戏地图后选择）";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"通过标签选择地图（后台扫描已就绪） · mapClass={mapClass} · choices={choices.Count}");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _scanProgressOverlay.Report(0.85d, "正在打开候选界面...");
            var candidateResolution = await ResolveCandidateSelectionAsync(
                frame,
                choices,
                $"通过标签选择地图 · 共 {choices.Count} 项候选",
                mapClass,
                cancellationToken);

            if (candidateResolution.StartSurvey)
            {
                await ActivateSurveyFromQuickScanAsync(
                    frame,
                    operationMatch,
                    cancellationToken);
                return;
            }

            if (candidateResolution.Recognition is { } chosen)
            {
                await AlignAndPublishSelectedTagMapAsync(
                    operationMatch,
                    chosen,
                    frame,
                    cancellationToken);
            }
            else
            {
                _statusMessage = "已取消地图选择。";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (ownsFrame)
            {
                frame?.Dispose();
            }
        }
    }

    private async Task AlignAndPublishSelectedTagMapAsync(
        MapMatchSnapshot operationMatch,
        RuntimeMapRecognition chosen,
        CapturedGameFrame initialFrame,
        CancellationToken cancellationToken)
    {
        var locked = LockSelectedMapIdentity(chosen, initialFrame, userConfirmed: true);
        var targetFloorKey = ResolveBackgroundConsumeFloorKey(locked);
        _pendingAlignmentSeed = CreateIndependentFloorSeedSession(locked, targetFloorKey);

        _scanProgressOverlay.Report(0.90d, "正在对齐所选地图...");
        CapturedGameFrame? alignmentFrame = null;
        try
        {
            alignmentFrame = await CaptureBackgroundAlignmentFrameAsync(
                locked.Map,
                cancellationToken,
                () => IsCurrentMatchOperation(operationMatch));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logCollector.Append(
                MapLogCategory.ViewportCapture,
                MapLogLevel.Warning,
                $"通过标签选择地图抓取对齐帧异常：{ex.Message}");
        }

        if (alignmentFrame is null)
        {
            _statusMessage = $"已选择地图：{locked.Map.DisplayName}（当前未捕获到地图画面，打开游戏地图后将自动对齐）";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                _statusMessage);
            RefreshMiniMapForCurrentFloor();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            if (alignmentFrame.DetectedFloorKey is { } detectedFloor && !string.IsNullOrWhiteSpace(detectedFloor))
            {
                targetFloorKey = detectedFloor;
                PresentDetectedFloorBeforeAlignment(locked, detectedFloor, alignmentFrame);
                _pendingAlignmentSeed = CreateIndependentFloorSeedSession(locked, targetFloorKey);
            }

            var alignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (alignmentTuning.GateTemplateThreshold > GateTemplateRules.FallbackPairThreshold)
            {
                alignmentTuning.GateTemplateThreshold = GateTemplateRules.FallbackPairThreshold;
            }
            var structureTuning = CreateStructureTuningForFloor(
                locked.Map,
                targetFloorKey,
                CreateInitialAlignmentStructureTuning());

            MapRecognitionAttempt attempt;
            MapFeatureCacheKey? repairCacheKey = null;

            try
            {
                attempt = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapId = locked.Map.Id;
                    if (_recognition.TryGetMap(mapId) is { } targetMap)
                    {
                        if (_recognition.TryAlignWithVpsg3(
                                alignmentFrame,
                                targetMap,
                                targetFloorKey,
                                locked.Result.IdentityConfidence,
                                out var fastVpsgAttempt,
                                out var tagScanVpsgStatus))
                        {
                            return fastVpsgAttempt;
                        }

                        NotifyVpsg3DegradationIfNeeded(mapId, targetFloorKey, tagScanVpsgStatus);
                    }

                    if (!string.Equals(
                            targetFloorKey,
                            MapFloorRules.GetPrimaryFloorKey(locked.Map),
                            StringComparison.Ordinal))
                    {
                        return _recognition.AlignFloorWithoutGates(
                            alignmentFrame,
                            mapId,
                            targetFloorKey,
                            MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                                locked.Map,
                                targetFloorKey),
                            _settings!.OverlayAlignmentMode,
                            alignmentTuning,
                            structureTuning,
                            allowPrimaryFloor: false);
                    }

                    return AlignExactManualFloor(
                        alignmentFrame,
                        locked,
                        targetFloorKey,
                        MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                            locked.Map,
                            targetFloorKey),
                        _settings!.OverlayAlignmentMode,
                        alignmentTuning,
                        structureTuning,
                        locked.Result.IdentityConfidence,
                        out repairCacheKey);
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Error,
                    $"通过标签选择地图执行对齐异常：{ex.Message}");
                _statusMessage = $"已锁定所选地图：{locked.Map.DisplayName}，但对齐异常：{ex.Message}；打开游戏地图后将重试对齐。";
                RefreshMiniMapForCurrentFloor();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
            {
                return;
            }

            _lastDiagnostics = attempt.Diagnostics;
            var aligned = attempt.Recognition;

            if (aligned is null)
            {
                var failureReason = attempt.FailureReason;
                _statusMessage = $"已锁定地图：{locked.Map.DisplayName}，当前画面对齐未通过：{failureReason ?? "未匹配到特征"}；请保持地图打开后重试。";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Warning,
                    _statusMessage);

                var failurePresent = _overlay.DeferPresent();
                try
                {
                    ShowTransientOverlayStatus(
                        MapOverlayStatusLevel.Warning,
                        "已选定地图，等待开图对齐",
                        _statusMessage,
                        "已锁定所选地图身份；请保持完整地图打开并重新打开地图以完成对齐。",
                        alignmentFrame.ClientBounds,
                        alignmentFrame.WindowHandle);
                    _overlay.Show();
                }
                finally
                {
                    failurePresent.Dispose();
                }
                RefreshMiniMapForCurrentFloor();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_settings!.RecognitionTuning.PlayerDecidesScale
                && aligned.Result.OverlayTransform is { } initialTransform)
            {
                var playerTransform = await MapManualTransformWindow.ShowAsync(
                    alignmentFrame,
                    aligned,
                    initialTransform,
                    cancellationToken,
                    _captureProtection);
                if (playerTransform is { } chosenPlayerTransform)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch))
                    {
                        return;
                    }
                    aligned = WithOverlayTransform(aligned, chosenPlayerTransform);
                }
            }

            var adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                aligned,
                alignmentFrame,
                _lastDiagnostics);
            aligned = adaptiveDecision.RecognitionToRender;

            RememberMapViewportPresenceReference(aligned, alignmentFrame);
            if (adaptiveDecision.AllowLegacyCacheWrite)
            {
                if (repairCacheKey is not null)
                {
                    await RepairMapCacheAsync(repairCacheKey, aligned, alignmentFrame);
                }
                await PersistPreprocessedScaleAsync(
                    aligned,
                    alignmentFrame,
                    _lastDiagnostics);
                RecordSuccessfulAlignment(aligned, alignmentFrame);
            }

            if (aligned.Result.OverlayTransform is { } committedTransform)
            {
                _mapOpenSession.LockAlignedMap(
                    aligned.Map.Id,
                    aligned.Result.Floor,
                    MapSimilarityTransform.FromOverlay(committedTransform),
                    aligned.Result.EvidenceKind switch
                    {
                        MapAlignmentEvidenceKind.DualGate => MapLocationMethod.DualAnchor,
                        MapAlignmentEvidenceKind.SingleGateAndAuxiliary => MapLocationMethod.SingleAnchor,
                        MapAlignmentEvidenceKind.AuxiliaryConsensus => MapLocationMethod.AuxiliaryAnchor,
                        MapAlignmentEvidenceKind.Structure => MapLocationMethod.StructureTranslation,
                        _ => MapLocationMethod.Manual
                    },
                    aligned.Result.LocalizationConfidence);
            }

            _currentFloorKey = aligned.Result.Floor;
            _lastRecognition = aligned;
            _mapLease.Bind(_matchSession.Snapshot, aligned.Map.Id);
            _pendingAlignmentIdentity = null;
            _pendingAlignmentSeed = null;
            var updatedSession = UpdateAlignmentSession(
                _lastAlignmentSession ?? _pendingAlignmentSeed,
                aligned);
            _lastAlignmentSession = updatedSession;
            if (adaptiveDecision.AllowReliableSession)
            {
                RememberPrimaryFloorSession(aligned, updatedSession);
            }
            RememberReliableFloorAlignment(
                operationMatch,
                aligned,
                updatedSession,
                alignmentFrame,
                adaptiveDecision.AllowReliableSession);
            _lastGameBounds = alignmentFrame.ClientBounds;
            _lastGameWindowHandle = alignmentFrame.WindowHandle;
            _statusMessage = $"地图已对齐：{aligned.Map.DisplayName} · {aligned.Result.Floor.ToUpperInvariant()}";
            _hasCompletedQuickScanAlignment = true;

            _gameMapToggleState.MarkOpen();
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"通过标签选择地图并完成对齐 · map={aligned.Map.DisplayName} · floor={aligned.Result.Floor}，已同步原生地图为打开状态。");

            var present = _overlay.DeferPresent();
            try
            {
                _overlay.SetMainContentVisible(true);
                if (!_overlay.TryUpdateMapTransformOnly(
                        aligned,
                        alignmentFrame.ClientBounds,
                        alignmentFrame.WindowHandle,
                        alignmentFrame.ViewportBounds))
                {
                    _overlay.UpdateMap(
                        aligned,
                        alignmentFrame.ClientBounds,
                        alignmentFrame.WindowHandle,
                        _settings!.ShowOverlayStatus,
                        alignmentFrame.ViewportBounds);
                }
                if (adaptiveDecision.AllowReliableSession)
                {
                    ShowAdaptiveReliableStatus(
                        aligned,
                        adaptiveDecision,
                        alignmentFrame.ClientBounds,
                        alignmentFrame.WindowHandle);
                }
                else
                {
                    ShowAdaptiveProvisionalStatus(
                        aligned,
                        adaptiveDecision,
                        alignmentFrame.ClientBounds,
                        alignmentFrame.WindowHandle);
                }
                _overlay.Show();
            }
            finally
            {
                present.Dispose();
            }

            if (adaptiveDecision.StartOrbTracking)
            {
                await StartOrbTrackingAsync(aligned, alignmentFrame);
            }
            RefreshMiniMapForCurrentFloor();
            _scanProgressOverlay.Report(1.0d, "对齐完成");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            alignmentFrame?.Dispose();
        }
    }
}
