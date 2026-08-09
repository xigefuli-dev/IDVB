using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage : UserControl
{
    private void Runtime_StateChanged(object? sender, EventArgs e)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() => TryRefresh("state-changed"));
        }
        catch (Exception exception)
        {
            ReportPageFailure("queue-state-change", exception);
        }
    }

    private void TryRefresh(string stage)
    {
        try
        {
            RefreshCore();
        }
        catch (Exception exception)
        {
            _refreshing = false;
            ReportPageFailure(stage, exception);
            try
            {
                _status.Text =
                    $"状态数据刷新失败，页面其余功能仍可使用：{exception.Message}";
            }
            catch (Exception statusException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Map status page could not show its failure: {statusException}");
            }
        }
    }

    private void Refresh() => TryRefresh("interaction");

    private void RefreshCore()
    {
        _refreshing = true;
        _enabledToggle.IsOn = _runtime.Settings.IsEnabled;
        _allowAutomaticMapCacheToggle.IsOn =
            _runtime.Settings.AllowAutomaticMapCache;
        _overlayStatusToggle.IsOn = _runtime.Settings.ShowOverlayStatus;
        _alignmentMode.SelectedItem = _alignmentMode.Items
            .OfType<AlignmentModeChoice>()
            .FirstOrDefault(choice =>
                choice.Mode == _runtime.Settings.OverlayAlignmentMode);
        var tuning = _runtime.Settings.RecognitionTuning;
        _gateThreshold.Value = tuning.GateTemplateThreshold * 100d;
        _minimumConfidence.Value = tuning.MinimumConfidence * 100d;
        _vectorTolerance.Value = tuning.VectorErrorTolerance;
        _ambiguityMargin.Value = tuning.AmbiguityMargin;
        _confirmationAdvantage.Value = tuning.ConfirmationAdvantage;
        _forceBestResultToggle.IsOn = tuning.ForceBestRecognitionResult;
        _forceCandidateToggle.IsOn = tuning.ForceCandidateSelection;
        _skipFloorRecognitionToggle.IsOn = _runtime.Settings.SkipFloorRecognition;
        var sessionTuning = _runtime.Settings.SessionTuning;
        _skipStabilityConfirmationToggle.IsOn =
            sessionTuning.SkipStabilityConfirmation;
        _highConfidenceThreshold.Value = sessionTuning.HighConfidence * 100d;
        _mediumConfidenceThreshold.Value =
            sessionTuning.MediumConfidence * 100d;
        _openingAnimationDelay.Value = sessionTuning.OpeningAnimationDelayMilliseconds;
        _openingTimeout.Value = sessionTuning.OpeningTimeoutMilliseconds;
        _stableFrameCount.Value = sessionTuning.StableFrameCount;
        _stableFrameInterval.Value = sessionTuning.StableFrameIntervalMilliseconds;
        _stableFrameDifference.Value = sessionTuning.StableFrameDifference;
        _mediumConfidenceFrames.Value = sessionTuning.MediumConfidenceFrames;
        _candidateStabilityPixels.Value = sessionTuning.CandidateStabilityPixels;
        _nativeScaleChangeRatio.Value = sessionTuning.NativeScaleChangeRatio * 100d;
        _warmGateSearchBudget.Value = tuning.WarmGateSearchBudgetMs;
        _confirmationGateSearchBudget.Value = tuning.ConfirmationGateSearchBudgetMs;
        _confirmationRoiPaddingFactor.Value =
            tuning.ConfirmationRoiTemplatePaddingFactor;
        _confirmationRoiMinimumPadding.Value =
            tuning.ConfirmationRoiMinimumPaddingPixels;
        _confirmationMaximumMapDrag.Value =
            tuning.ConfirmationMaximumMapDragPixelsPerSecond;
        var floorTuning = _runtime.Settings.FloorRecognitionTuning;
        _floorMinimumConfidence.Value = floorTuning.MinimumConfidence * 100d;
        _floorLocalizationConfidence.Value =
            floorTuning.MinimumLocalizationConfidence * 100d;
        _floorRecognitionTimeout.Value =
            floorTuning.MaximumRecognitionWindowMilliseconds;
        _floorFirstConfirmationFrames.Value =
            floorTuning.FirstFloorConfirmationFrames;
        _floorSecondConfirmationFrames.Value =
            floorTuning.SecondFloorConfirmationFrames;
        var playerTuning = _runtime.Settings.PlayerTrackingTuning;
        _playerMinimumConfidence.Value = playerTuning.MinimumConfidence * 100d;
        _playerFailureLimit.Value = playerTuning.LocalSearchFailureLimit;
        _playerStaleHideMilliseconds.Value = playerTuning.StaleHideMilliseconds;
        var structureTuning = _runtime.Settings.StructureRegistrationTuning;
        _auxiliaryAnchorToggle.IsOn =
            structureTuning.UseAuxiliaryAnchorRecognition;
        _reusePreviousAlignmentToggle.IsOn =
            structureTuning.ReusePreviousAlignmentResult;
        _previousAlignmentSearchRadius.Value =
            structureTuning.PreviousAlignmentSearchRadiusPixels;
        _structureEccToggle.IsOn = structureTuning.EnableEccRefinement;
        _structureDebugToggle.IsOn = structureTuning.EnableDebugOutput;
        _structureChamfer.Value = structureTuning.MaximumChamferPixels;
        _structureCoverage.Value = structureTuning.MinimumEdgeCoverage * 100d;
        _structureMargin.Value = structureTuning.MinimumCandidateMargin * 100d;
        _auxiliaryMaxTemplates.Value = structureTuning.MaximumAuxiliaryTemplates;
        _auxiliaryDirectLockConfidence.Value =
            structureTuning.AuxiliaryDirectLockConfidence * 100d;
        _viewportEdgeMargin.Value =
            structureTuning.MapViewportEdgeMargin * 100d;
        _featureVotingToggle.IsOn = structureTuning.EnableFeatureVoting;
        _fastAlignmentToggle.IsOn = structureTuning.EnableFastAlignment;
        _fastShadowToggle.IsOn = structureTuning.FastAlignmentShadowMode;
        _visibleAwareToggle.IsOn = structureTuning.EnableVisibleMask
            && structureTuning.EnableVisibleAwareInjection;
        _featureRatioThreshold.Value = structureTuning.FeatureRatioThreshold * 100d;
        _featureInlierTolerance.Value = structureTuning.FeatureInlierTolerancePixels;
        _featureMaxCandidates.Value = structureTuning.MaximumTranslationCandidates;
        _fastCoarseDownsample.Value = structureTuning.FastCoarseDownsampleFactor;
        _fastCoarseTopK.Value = structureTuning.FastCoarseTopK;
        _structureOccupancy.Value = structureTuning.MinimumOccupancyCoverage * 100d;
        _structurePartitions.Value = structureTuning.MinimumConsistentPartitions;
        _structureEdgeTolerance.Value = structureTuning.EdgeDistanceTolerancePixels;
        _structureTopCandidates.Value = structureTuning.TopCandidateCount;
        _structureBudget.Value = structureTuning.StructureFallbackBudgetMilliseconds;
        _firstScanStrategyToggle.IsOn =
            _runtime.Settings.FirstScanStrategy == FirstScanStrategy.SideEntrance;

        // 预设选择器
        if (_presetSelector.Items.Count == 0)
        {
            _presetSelector.ItemsSource = _runtime.GetAvailablePresets();
            var activePreset = _runtime.GetActivePreset();
            foreach (IDVBuff.Core.Models.ResolutionTuningProfile p in _presetSelector.Items)
            {
                if (p.Name == activePreset)
                {
                    _presetSelector.SelectedItem = p;
                    break;
                }
            }
        }
        _sideEntranceFeatureRadius.Value = tuning.SideEntranceFeatureRadius;
        var controlsEnabled = !_runtime.IsScanning;
        _allowAutomaticMapCacheToggle.IsEnabled = controlsEnabled;
        _gateThreshold.IsEnabled = controlsEnabled;
        _minimumConfidence.IsEnabled = controlsEnabled;
        _highConfidenceThreshold.IsEnabled = controlsEnabled;
        _vectorTolerance.IsEnabled = controlsEnabled;
        _ambiguityMargin.IsEnabled = controlsEnabled;
        _confirmationAdvantage.IsEnabled = controlsEnabled;
        _forceBestResultToggle.IsEnabled = controlsEnabled;
        _forceCandidateToggle.IsEnabled = controlsEnabled;
        _skipFloorRecognitionToggle.IsEnabled = controlsEnabled;
        _skipStabilityConfirmationToggle.IsEnabled = controlsEnabled;
        _mediumConfidenceThreshold.IsEnabled = controlsEnabled;
        _openingAnimationDelay.IsEnabled = controlsEnabled;
        _openingTimeout.IsEnabled = controlsEnabled;
        _stableFrameCount.IsEnabled = controlsEnabled;
        _stableFrameInterval.IsEnabled = controlsEnabled;
        _stableFrameDifference.IsEnabled = controlsEnabled;
        _mediumConfidenceFrames.IsEnabled = controlsEnabled;
        _candidateStabilityPixels.IsEnabled = controlsEnabled;
        _nativeScaleChangeRatio.IsEnabled = controlsEnabled;
        _warmGateSearchBudget.IsEnabled = controlsEnabled;
        _confirmationGateSearchBudget.IsEnabled = controlsEnabled;
        _confirmationRoiPaddingFactor.IsEnabled = controlsEnabled;
        _confirmationRoiMinimumPadding.IsEnabled = controlsEnabled;
        _confirmationMaximumMapDrag.IsEnabled = controlsEnabled;
        _floorMinimumConfidence.IsEnabled = controlsEnabled;
        _floorLocalizationConfidence.IsEnabled = controlsEnabled;
        _floorRecognitionTimeout.IsEnabled = controlsEnabled;
        _floorFirstConfirmationFrames.IsEnabled = controlsEnabled;
        _floorSecondConfirmationFrames.IsEnabled = controlsEnabled;
        _playerMinimumConfidence.IsEnabled = controlsEnabled;
        _playerFailureLimit.IsEnabled = controlsEnabled;
        _playerStaleHideMilliseconds.IsEnabled = controlsEnabled;
        _allowExtendToggle.IsEnabled = controlsEnabled;
        _miniMapEnabledToggle.IsEnabled = controlsEnabled;
        _miniMapScaleBox.IsEnabled =
            controlsEnabled && _miniMapEnabledToggle.IsOn;
        _auxiliaryAnchorToggle.IsEnabled = controlsEnabled;
        _reusePreviousAlignmentToggle.IsEnabled = controlsEnabled;
        _previousAlignmentSearchRadius.IsEnabled =
            controlsEnabled && _reusePreviousAlignmentToggle.IsOn;
        _structureEccToggle.IsEnabled = controlsEnabled;
        _structureDebugToggle.IsEnabled = controlsEnabled;
        _structureChamfer.IsEnabled = controlsEnabled;
        _structureCoverage.IsEnabled = controlsEnabled;
        _structureMargin.IsEnabled = controlsEnabled;
        _auxiliaryMaxTemplates.IsEnabled =
            controlsEnabled && _auxiliaryAnchorToggle.IsOn;
        _auxiliaryDirectLockConfidence.IsEnabled =
            controlsEnabled && _auxiliaryAnchorToggle.IsOn;
        _viewportEdgeMargin.IsEnabled = controlsEnabled;
        _featureVotingToggle.IsEnabled = controlsEnabled;
        _visibleAwareToggle.IsEnabled = controlsEnabled;
        _featureRatioThreshold.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _featureInlierTolerance.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _featureMaxCandidates.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _fastCoarseDownsample.IsEnabled = controlsEnabled;
        _fastCoarseTopK.IsEnabled = controlsEnabled;
        _structureOccupancy.IsEnabled = controlsEnabled;
        _structurePartitions.IsEnabled = controlsEnabled;
        _structureEdgeTolerance.IsEnabled = controlsEnabled;
        _structureTopCandidates.IsEnabled = controlsEnabled;
        _structureBudget.IsEnabled = controlsEnabled;
        _alignmentMode.IsEnabled = controlsEnabled;
        _firstScanStrategyToggle.IsEnabled = controlsEnabled;
        _sideEntranceFeatureRadius.IsEnabled = controlsEnabled;
        _scanButton.IsEnabled =
            controlsEnabled && _runtime.MatchSnapshot.IsStarted;
        _manualButton.IsEnabled =
            controlsEnabled && _runtime.MatchSnapshot.IsStarted;
        _refreshing = false;

        _gameMapBinding.Text = $"当前：{_runtime.Settings.GameMapToggleBinding.DisplayName}";
        _controlPanelBinding.Text =
            $"当前：{_runtime.Settings.ControlPanelToggleBinding.DisplayName}";
        _quickBinding.Text = $"当前：{_runtime.Settings.QuickScanBinding.DisplayName}";
        _overlayBinding.Text = $"当前：{_runtime.Settings.OverlayToggleBinding.DisplayName}";
        _manualBinding.Text = $"当前：{_runtime.Settings.ManualRecognitionBinding.DisplayName}";
        _switchFloorBinding.Text = $"当前：{_runtime.Settings.SwitchFloorBinding.DisplayName}";
        _saveMapCacheBinding.Text =
            $"当前：{_runtime.Settings.SaveMapCacheBinding.DisplayName}";
        _overlayState.Text = _runtime.IsOverlayVisible
            ? "正在显示（鼠标与键盘穿透）"
            : "当前隐藏";
        _permissionState.Text = _runtime.IntegrityStatus.Message;
        _elevationButton.Visibility = _runtime.IntegrityStatus.RequiresElevation
            ? Visibility.Visible
            : Visibility.Collapsed;
        _calibrationState.Text = _runtime.Settings.IsMapViewportCalibrated
            ? $"已校准（基准 {_runtime.Settings.CalibrationClientWidth}×{_runtime.Settings.CalibrationClientHeight}）"
            : _runtime.Settings.MapViewportRegion?.IsValid is true
                ? "需要重新校准（旧校准区域已保留）"
                : "尚未校准";
        _floorCalibrationState.Text = _runtime.Settings.IsFloorDisplayCalibrated
            ? $"已校准（基准 {_runtime.Settings.FloorCalibrationClientWidth}×{_runtime.Settings.FloorCalibrationClientHeight}）"
            : _runtime.Settings.FloorDisplayRegion?.IsValid is true
                ? "需要重新校准（旧校准区域已保留）"
                : "尚未校准";
        _playerCalibrationState.Text = _runtime.ArePlayerAssetsReady
            ? "4/4 张序号图片已就绪"
            : "玩家序号图片不完整；无法开始对局";
        _floorState.Text = _runtime.LastFloorRecognition is { } floorResult
            ? floorResult.Succeeded && floorResult.Floor is { } floor
                ? $"{floor.ToUpperInvariant()} · 置信度 {floorResult.Confidence:P0}"
                    + $" · 数字定位 {floorResult.LocalizationConfidence:P0}"
                    + (floorResult.LocalizedRegion is { } localized
                        ? $" · 内部区域 "
                            + $"{localized.X:P0},{localized.Y:P0} "
                            + $"{localized.Width:P0}×{localized.Height:P0}"
                        : string.Empty)
                    + $" · 捕获 {floorResult.CaptureMilliseconds:F1}ms"
                    + $" · 判定 {floorResult.AnalysisMilliseconds:F1}ms"
                    + $" · 端到端 {floorResult.EndToEndMilliseconds:F1}ms"
                    + (floorResult.EndToEndMilliseconds
                            > MapFloorRecognitionRules.PerformanceBudgetMilliseconds
                        ? " · 超过 100ms 性能目标"
                        : string.Empty)
                : $"未识别 · {floorResult.FailureReason}"
                    + $" · 端到端 {floorResult.EndToEndMilliseconds:F1}ms"
            : "尚无楼层识别结果";
        _mapReadiness.Text =
            $"{_runtime.ReadyMapCount}/{_runtime.TotalMapCount} 张地图已完成一楼区域与双门标记";
        _selectedMapState.Text = _runtime.SelectedMap is { } selectedMap
            ? selectedMap.DisplayName
            : "尚未快捷扫描或手动选择地图";
        _alignmentState.Text = FormatAlignmentState(
            _runtime.AlignmentTrackingMode);
        var snapshot = _runtime.SessionSnapshot;
        _sessionState.Text = FormatSessionSnapshot(snapshot);
        var match = _runtime.MatchSnapshot;
        _matchState.Text = match.IsStarted
            ? $"已开始 · 自己是 {(int)match.PlayerSlot!.Value} 号玩家 · 模式 {match.MapClass} · 版本 {match.Version}"
            : $"已结束 · 未选择玩家 · 版本 {match.Version}";
        _controlPanelState.Text = _runtime.IsControlPanelVisible
            ? "正在显示（可交互）"
            : "当前隐藏";
        _playerState.Text = snapshot.Player is { IsTrusted: true } player
            ? $"{(int)player.PlayerSlot} 号 · 完整地图 ({player.ReferencePoint.X:F1}, {player.ReferencePoint.Y:F1})"
                + $" · 视口 ({player.ViewportPoint.X:F1}, {player.ViewportPoint.Y:F1})"
                + $" · 置信度 {player.Confidence:P0}"
            : _runtime.LastTrustedPlayerPosition is { } prior
                ? $"当前图标隐藏；最后可信完整地图位置 ({prior.X:F1}, {prior.Y:F1})"
                : "尚无可信玩家位置";
        _lastResult.Text = _runtime.LastRecognition is { } recognition
            ? FormatRecognition(recognition)
            : "尚无识别结果";
        _timings.Text = _runtime.LastDiagnostics is { } diagnostics
            ? diagnostics.ToStatusText()
                + (diagnostics.StructureSearchMilliseconds > 0d
                    ? $" · 候选 {diagnostics.StructureCandidateCount}"
                        + $" · 最佳/次佳 {diagnostics.StructureBestScore:F3}/{diagnostics.StructureSecondScore:F3}"
                        + $" · 差距 {diagnostics.StructureCandidateMargin:P1}"
                        + $" · AKAZE 内点 {diagnostics.StructureFeatureInlierCount}/{diagnostics.StructureFeatureMatchCount}"
                        + $" · 聚类 {diagnostics.StructureFeatureConsensus:P0}"
                        + (diagnostics.StructureEccConverged
                            ? $" · ECC {diagnostics.StructureEccCorrelation:F3}"
                            : " · ECC 未收敛")
                    : string.Empty)
            : "尚无扫描数据";
        _collectLogsToggle.IsOn = _runtime.Settings.CollectLogs;
        _collectResearchToggle.IsOn =
            _runtime.Settings.CollectAlignmentResearchData;
        _allowExtendToggle.IsOn = _runtime.Settings.AllowMapExtendBeyondBounds;
        _miniMapEnabledToggle.IsOn = _runtime.Settings.PersistentMiniMapEnabled;
        _playerTrackingToggle.IsOn = _runtime.Settings.PlayerTrackingEnabled;
        _reverseAlternateDisplayToggle.IsOn = _runtime.Settings.ReverseAlternateDisplay;
        _reverseAlternateDisplayToggle.IsEnabled = controlsEnabled
            && !_overlayStatusToggle.IsOn;
        _showGateMarkersToggle.IsOn = _runtime.Settings.ShowGateMarkers;
        _showAuxiliaryAnchorsToggle.IsOn = _runtime.Settings.ShowAuxiliaryAnchors;
        _showTextAnnotationsToggle.IsOn = _runtime.Settings.ShowTextAnnotations;
        _showBoxAnnotationsToggle.IsOn = _runtime.Settings.ShowBoxAnnotations;
        _showGateMarkersOnMiniMapToggle.IsOn = _runtime.Settings.ShowGateMarkersOnMiniMap;
        _showAuxiliaryAnchorsOnMiniMapToggle.IsOn = _runtime.Settings.ShowAuxiliaryAnchorsOnMiniMap;
        _showTextAnnotationsOnMiniMapToggle.IsOn = _runtime.Settings.ShowTextAnnotationsOnMiniMap;
        _showBoxAnnotationsOnMiniMapToggle.IsOn = _runtime.Settings.ShowBoxAnnotationsOnMiniMap;
        _showFloorOnMiniMapToggle.IsOn = _runtime.Settings.ShowFloorOnMiniMap;
        _miniMapScaleBox.Value = _runtime.Settings.MiniMapScale * 100d;
        _miniMapOpacityBox.Value = _runtime.Settings.MiniMapOpacity * 100d;
        _miniMapOffsetXBox.Value = _runtime.Settings.MiniMapOffsetX;
        _miniMapOffsetYBox.Value = _runtime.Settings.MiniMapOffsetY;
        _statusOpacityBox.Value = _runtime.Settings.StatusOpacity * 100d;
        _statusOffsetXBox.Value = _runtime.Settings.StatusOffsetX;
        _statusOffsetYBox.Value = _runtime.Settings.StatusOffsetY;
        _logState.Text = _runtime.LogCollector.IsEnabled
            ? $"正在收集 · {_runtime.LogCollector.EntryCount} 条"
            : "已关闭";
        _researchState.Text = _runtime.ResearchCollector.IsEnabled
            ? $"正在采集 · {_runtime.ResearchCollector.RecordCount} 条 · "
                + (_runtime.ResearchCollector.CurrentSessionDirectory
                    ?? _runtime.ResearchCollector.RootDirectory)
            : $"已关闭 · {_runtime.ResearchCollector.RootDirectory}";
        _status.Text = _runtime.StatusMessage;
    }

    private static string FormatSessionSnapshot(MapSessionSnapshot snapshot)
    {
        var summary =
            $"{snapshot.State} · {snapshot.LocationMethod}"
            + $" · 会话版本 {snapshot.Version}"
            + $" · 对齐修订 {snapshot.AlignmentRevision}"
            + (snapshot.MapId is { } mapId
                ? $" · 地图 {mapId.ToString("N")[..8]}"
                : string.Empty)
            + (snapshot.Floor is { } floor
                ? $" · {floor.ToUpperInvariant()}"
                : string.Empty)
            + (snapshot.State == MapSessionState.LowConfidence
                    || snapshot.Confidence > 0d
                ? $" · 置信度 {snapshot.Confidence:P1}"
                : string.Empty)
            + (snapshot.StableCandidateFrames > 0
                ? $" · 稳定帧 {snapshot.StableCandidateFrames}"
                : string.Empty);
        if (snapshot.ViewportOrigin is { } origin)
            summary += $" · 视口原点 ({origin.X:F1}, {origin.Y:F1})";
        if (snapshot.LockedTransform is { } transform)
        {
            summary +=
                $" · S={transform.Scale:F4}"
                + $" R={transform.RotationDegrees:F1}°"
                + $" T=({transform.TranslationX:F1}, {transform.TranslationY:F1})";
        }
        if (snapshot.RecalibrationReason != MapRecalibrationReason.None)
            summary += $" · 失效原因 {snapshot.RecalibrationReason}";
        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
            summary += $" · {snapshot.Detail}";
        return summary;
    }

    private static string FormatRecognition(RuntimeMapRecognition recognition)
    {
        var source = recognition.Result.Source switch
        {
            MapRecognitionSource.ManualGateSelection => "手动门点",
            MapRecognitionSource.UserConfirmed => "手动确认",
            MapRecognitionSource.SelectedMapGatePair => "双门完整对齐",
            MapRecognitionSource.SingleGateTracking => "单门跟踪（缩放锁定）",
            MapRecognitionSource.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
            MapRecognitionSource.StructureMatching => "局部地图结构配准",
            MapRecognitionSource.ReusedLastTransform => "复用上次可靠对齐",
            _ => "自动识别"
        };
        var summary =
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()} · {recognition.Result.Confidence:P0} · {source}"
            + $" · 证据 {recognition.Result.EvidenceKind}"
            + (recognition.Result.SkippedStructureValidation
                ? " · 已跳过结构复核"
                : $" · 结构 {recognition.Result.StructureDisposition}")
            + (recognition.Result.WasForcedBestResult
                ? " · 强制呈现"
                : string.Empty);
        if (recognition.Result.OverlayTransform is not { } transform)
            return summary;
        return summary
            + $" · {transform.AlignmentMode.ToDisplayName()}"
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 平均边缘距离 {transform.MaximumResidualPixels:F1}px"
                : $" · 最大误差 {transform.MaximumResidualPixels:F1}px")
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 候选差距 {recognition.Result.StructureCandidateMargin:P1}"
                : string.Empty)
            + (transform.UsedDegenerateAxisFallback ? " · 退化轴回退" : string.Empty)
            + (transform.IsExactFit ? " · 已贴合" : " · 未完全贴合");
    }

    private static string FormatAlignmentState(
        MapAlignmentTrackingMode mode) => mode switch
    {
        MapAlignmentTrackingMode.NeedsGatePair => "需要双门完成本次运行的缩放锁定",
        MapAlignmentTrackingMode.GatePairLocked => "双门完整对齐",
        MapAlignmentTrackingMode.SingleGateTracking => "单门跟踪（缩放锁定）",
        MapAlignmentTrackingMode.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
        MapAlignmentTrackingMode.WaitingForAnchor => "等待可信门点或结构证据恢复",
        MapAlignmentTrackingMode.StructureMatched => "局部地图结构配准",
        MapAlignmentTrackingMode.HoldingLastTransform => "结构证据不足，已保留最后可靠对齐",
        MapAlignmentTrackingMode.Lost => "对齐已失效，需要双门重新锁定",
        _ => "尚无运行时对齐"
    };
}
