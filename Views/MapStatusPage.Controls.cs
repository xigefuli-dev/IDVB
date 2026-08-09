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
    private async Task CalibrateViewportAsync()
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "准备校准地图区域",
            Content =
                "点击开始后，请在 3 秒内切换到第五人格并打开完整地图。"
                + "随后框选整张地图画布的外边缘，不要只框建筑主体或两个门。"
                + "程序只保存相对坐标，截图不会写入磁盘。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;

        _status.Text = "请切换到游戏，3 秒后捕获完整地图……";
        await Task.Delay(3000);
        if (!_runtime.TryCaptureCalibrationFrame(out var frame, out var failureReason)
            || frame is null)
        {
            _status.Text = failureReason;
            return;
        }

        using (frame)
        {
            ((App)Application.Current).MainWindow.Activate();
            var region = await MapViewportCalibrationDialog.ShowAsync(
                XamlRoot,
                frame,
                _runtime.Settings.ResolveMapViewportRegion(
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height)),
                "校准游戏地图区域",
                "请沿完整地图画布的外边缘框选，不要只框建筑主体、两个门或它们之间的区域。只保存相对坐标，截图不会写入磁盘。");
            if (region is null)
                return;
            await _runtime.SetMapViewportAsync(
                region,
                (int)Math.Round(frame.ClientBounds.Width),
                (int)Math.Round(frame.ClientBounds.Height),
                DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle));
        }
        Refresh();
    }

    private async Task CalibrateFloorDisplayAsync()
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "准备校准楼层显示区",
            Content =
                "点击开始后，请在 3 秒内切换到第五人格并打开完整地图。"
                + "随后完整框选包含 1 和 2 两个按钮的楼层显示区域。"
                + "程序只保存相对坐标，截图不会写入磁盘。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;

        _status.Text = "请切换到游戏，3 秒后捕获楼层显示区……";
        await Task.Delay(3000);
        if (!_runtime.TryCaptureCalibrationFrame(out var frame, out var failureReason)
            || frame is null)
        {
            _status.Text = failureReason;
            return;
        }

        using (frame)
        {
            ((App)Application.Current).MainWindow.Activate();
            var region = await MapViewportCalibrationDialog.ShowAsync(
                XamlRoot,
                frame,
                _runtime.Settings.ResolveFloorDisplayRegion(
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height)),
                "校准楼层显示区",
                "请完整框选 1F/2F 双按钮区域，保留两个按钮及其高亮背景；不要只框单个数字。只保存相对坐标，截图不会写入磁盘。");
            if (region is null)
                return;
            try
            {
                await _runtime.SetFloorDisplayRegionAsync(
                    region,
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height),
                    DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle));
            }
            catch (InvalidOperationException exception)
            {
                _status.Text = exception.Message;
                return;
            }
        }
        Refresh();
    }

    private async Task RunDelayedAsync(
        Button button,
        string idleText,
        Func<Task> action)
    {
        button.IsEnabled = false;
        button.Content = "请切换到游戏……";
        await Task.Delay(3000);
        await action();
        button.Content = idleText;
        button.IsEnabled = true;
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetEnabledAsync(_enabledToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AllowAutomaticMapCache_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetAllowAutomaticMapCacheAsync(
                _allowAutomaticMapCacheToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void OverlayStatusToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetOverlayStatusVisibleAsync(_overlayStatusToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ReverseAlternateDisplay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetReverseAlternateDisplayAsync(
                _reverseAlternateDisplayToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ShowGateMarkers_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowGateMarkersAsync(_showGateMarkersToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowAuxiliaryAnchors_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowAuxiliaryAnchorsAsync(_showAuxiliaryAnchorsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowTextAnnotations_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowTextAnnotationsAsync(_showTextAnnotationsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowBoxAnnotations_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowBoxAnnotationsAsync(_showBoxAnnotationsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowGateMarkersOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowGateMarkersOnMiniMapAsync(_showGateMarkersOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowAuxiliaryAnchorsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowAuxiliaryAnchorsOnMiniMapAsync(_showAuxiliaryAnchorsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowTextAnnotationsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowTextAnnotationsOnMiniMapAsync(_showTextAnnotationsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowBoxAnnotationsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowBoxAnnotationsOnMiniMapAsync(_showBoxAnnotationsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowFloorOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowFloorOnMiniMapAsync(_showFloorOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapScaleBox_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapScaleAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOpacity_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOpacityAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOffsetX_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOffsetXAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOffsetY_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOffsetYAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOpacity_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOpacityAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOffsetX_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOffsetXAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOffsetY_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOffsetYAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void CollectLogsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetCollectLogsAsync(_collectLogsToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void CollectResearchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetCollectAlignmentResearchDataAsync(
                _collectResearchToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void FirstScanStrategy_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            var strategy = _firstScanStrategyToggle.IsOn
                ? FirstScanStrategy.SideEntrance
                : FirstScanStrategy.DoubleGate;
            await _runtime.SetFirstScanStrategyAsync(strategy);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

    private async void PresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (_presetSelector.SelectedItem is not IDVBuff.Core.Models.ResolutionTuningProfile profile) return;
        try
        {
            await _runtime.SetActivePresetAsync(profile.Name);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }
        Refresh();
    }

    private async void SideEntranceFeatureRadius_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;
        var newRadius = (int)Math.Round(args.NewValue);
        if (newRadius == _runtime.Settings.RecognitionTuning.SideEntranceFeatureRadius)
            return;
        try
        {
            // 先保存新半径
            var tuning = _runtime.Settings.RecognitionTuning.Clone();
            tuning.SideEntranceFeatureRadius = newRadius;
            await _runtime.SetRecognitionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            return;
        }

        // 弹出进度对话框，批量重建特征图
        await ShowSideEntranceFeatureRebuildDialogAsync();
    }

    private async Task ShowSideEntranceFeatureRebuildDialogAsync()
    {
        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            MinWidth = 320
        };
        var progressText = new TextBlock
        {
            Text = "准备中……",
            Margin = new Thickness(0, 6, 0, 0)
        };
        var content = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock
                {
                    Text = "即将对所有地图重新生成侧门特征素材，处理时间取决于地图数量。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                },
                progressBar,
                progressText
            }
        };
        var dialog = new ContentDialog
        {
            Title = "重新预处理侧门特征",
            Content = content,
            XamlRoot = XamlRoot
        };

        var cts = new CancellationTokenSource();
        var progress = new Progress<(int done, int total)>(report =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var (done, total) = report;
                if (total > 0)
                {
                    progressBar.Maximum = total;
                    progressBar.Value   = done;
                    progressText.Text   = $"已处理 {done} / {total} 张";
                }
            });
        });

        // 异步启动重建，完成后关闭对话框
        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.RebuildSideEntranceFeaturesAsync(progress, cts.Token);
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => dialog.Hide());
            }
        });

        await dialog.ShowAsync();
    }

    private async void SkipFloorRecognition_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try        {
            await _runtime.SetSkipFloorRecognitionAsync(
                _skipFloorRecognitionToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void SkipStabilityConfirmation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetSkipStabilityConfirmationAsync(
                _skipStabilityConfirmationToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AllowExtendToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetAllowMapExtendBeyondBoundsAsync(_allowExtendToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void MiniMapEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPersistentMiniMapEnabledAsync(_miniMapEnabledToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void PlayerTracking_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPlayerTrackingEnabledAsync(_playerTrackingToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void MiniMapScale_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing)
            return;
        if (double.IsNaN(args.NewValue))
            return;
        try
        {
            await _runtime.SetMiniMapScaleAsync(args.NewValue / 100d);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AlignmentMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_refreshing
            || _alignmentMode.SelectedItem is not AlignmentModeChoice choice)
        {
            return;
        }
        try
        {
            await _runtime.SetOverlayAlignmentModeAsync(choice.Mode);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void Tuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveRecognitionTuningAsync();

    private async void SessionTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;
        try
        {
            await SaveSessionTuningAsync();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void FloorTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveFloorTuningAsync();

    private async void PlayerTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SavePlayerTuningAsync();

    private async void RecognitionTuning_Changed(
        object sender,
        RoutedEventArgs e) =>
        await SaveRecognitionTuningAsync();

    private async Task SaveRecognitionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.RecognitionTuning.Clone();
            tuning.GateTemplateThreshold = _gateThreshold.Value / 100d;
            tuning.MinimumConfidence = _minimumConfidence.Value / 100d;
            tuning.VectorErrorTolerance = _vectorTolerance.Value;
            tuning.AmbiguityMargin = _ambiguityMargin.Value;
            tuning.ConfirmationAdvantage = _confirmationAdvantage.Value;
            tuning.ForceBestRecognitionResult = _forceBestResultToggle.IsOn;
            tuning.ForceCandidateSelection = _forceCandidateToggle.IsOn;
            tuning.WarmGateSearchBudgetMs = (int)Math.Round(_warmGateSearchBudget.Value);
            tuning.ConfirmationGateSearchBudgetMs =
                (int)Math.Round(_confirmationGateSearchBudget.Value);
            tuning.ConfirmationRoiTemplatePaddingFactor =
                _confirmationRoiPaddingFactor.Value;
            tuning.ConfirmationRoiMinimumPaddingPixels =
                (int)Math.Round(_confirmationRoiMinimumPadding.Value);
            tuning.ConfirmationMaximumMapDragPixelsPerSecond =
                _confirmationMaximumMapDrag.Value;
            await _runtime.SetRecognitionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveSessionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.SessionTuning.Clone();
            tuning.HighConfidence = _highConfidenceThreshold.Value / 100d;
            tuning.MediumConfidence = _mediumConfidenceThreshold.Value / 100d;
            tuning.OpeningAnimationDelayMilliseconds =
                (int)Math.Round(_openingAnimationDelay.Value);
            tuning.OpeningTimeoutMilliseconds =
                (int)Math.Round(_openingTimeout.Value);
            tuning.StableFrameCount = (int)Math.Round(_stableFrameCount.Value);
            tuning.StableFrameIntervalMilliseconds =
                (int)Math.Round(_stableFrameInterval.Value);
            tuning.StableFrameDifference = _stableFrameDifference.Value;
            tuning.MediumConfidenceFrames =
                (int)Math.Round(_mediumConfidenceFrames.Value);
            tuning.CandidateStabilityPixels = _candidateStabilityPixels.Value;
            tuning.NativeScaleChangeRatio = _nativeScaleChangeRatio.Value / 100d;
            await _runtime.SetSessionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveFloorTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetFloorRecognitionTuningAsync(new MapFloorRecognitionTuning
            {
                MinimumConfidence = _floorMinimumConfidence.Value / 100d,
                MinimumLocalizationConfidence =
                    _floorLocalizationConfidence.Value / 100d,
                MaximumRecognitionWindowMilliseconds =
                    (int)Math.Round(_floorRecognitionTimeout.Value),
                FirstFloorConfirmationFrames =
                    (int)Math.Round(_floorFirstConfirmationFrames.Value),
                SecondFloorConfirmationFrames =
                    (int)Math.Round(_floorSecondConfirmationFrames.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SavePlayerTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetPlayerTrackingTuningAsync(new MapPlayerTrackingTuning
            {
                MinimumConfidence = _playerMinimumConfidence.Value / 100d,
                LocalSearchFailureLimit = (int)Math.Round(_playerFailureLimit.Value),
                StaleHideMilliseconds =
                    (int)Math.Round(_playerStaleHideMilliseconds.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void StructureTuning_Changed(object sender, RoutedEventArgs e) =>
        await SaveStructureTuningAsync();

    private async void StructureTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveStructureTuningAsync();

    private async Task SaveStructureTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.StructureRegistrationTuning.Clone();
            tuning.UseAuxiliaryAnchorRecognition = _auxiliaryAnchorToggle.IsOn;
            tuning.ReusePreviousAlignmentResult =
                _reusePreviousAlignmentToggle.IsOn;
            if (double.IsFinite(_previousAlignmentSearchRadius.Value))
            {
                tuning.PreviousAlignmentSearchRadiusPixels =
                    (int)Math.Round(_previousAlignmentSearchRadius.Value);
            }
            tuning.EnableEccRefinement = _structureEccToggle.IsOn;
            tuning.EnableDebugOutput = _structureDebugToggle.IsOn;
            tuning.MaximumChamferPixels = _structureChamfer.Value;
            tuning.MinimumEdgeCoverage = _structureCoverage.Value / 100d;
            tuning.MinimumCandidateMargin = _structureMargin.Value / 100d;
            if (double.IsFinite(_auxiliaryMaxTemplates.Value))
            {
                tuning.MaximumAuxiliaryTemplates =
                    (int)Math.Round(_auxiliaryMaxTemplates.Value);
            }
            if (double.IsFinite(_auxiliaryDirectLockConfidence.Value))
                tuning.AuxiliaryDirectLockConfidence =
                    _auxiliaryDirectLockConfidence.Value / 100d;
            if (double.IsFinite(_viewportEdgeMargin.Value))
                tuning.MapViewportEdgeMargin =
                    _viewportEdgeMargin.Value / 100d;
            tuning.EnableFeatureVoting = _featureVotingToggle.IsOn;
            tuning.EnableVisibleMask = _visibleAwareToggle.IsOn;
            tuning.EnableVisibleAwareInjection = _visibleAwareToggle.IsOn;
            tuning.EnableFastAlignment = _fastAlignmentToggle.IsOn;
            tuning.FastAlignmentShadowMode = _fastShadowToggle.IsOn;
            if (double.IsFinite(_featureRatioThreshold.Value))
                tuning.FeatureRatioThreshold = _featureRatioThreshold.Value / 100d;
            if (double.IsFinite(_featureInlierTolerance.Value))
                tuning.FeatureInlierTolerancePixels = _featureInlierTolerance.Value;
            if (double.IsFinite(_featureMaxCandidates.Value))
            {
                tuning.MaximumTranslationCandidates =
                    (int)Math.Round(_featureMaxCandidates.Value);
            }
            if (double.IsFinite(_fastCoarseDownsample.Value))
            {
                tuning.FastCoarseDownsampleFactor =
                    (int)Math.Round(_fastCoarseDownsample.Value);
            }
            if (double.IsFinite(_fastCoarseTopK.Value))
            {
                tuning.FastCoarseTopK = (int)Math.Round(_fastCoarseTopK.Value);
            }
            if (double.IsFinite(_structureOccupancy.Value))
                tuning.MinimumOccupancyCoverage = _structureOccupancy.Value / 100d;
            if (double.IsFinite(_structurePartitions.Value))
            {
                tuning.MinimumConsistentPartitions =
                    (int)Math.Round(_structurePartitions.Value);
            }
            if (double.IsFinite(_structureEdgeTolerance.Value))
                tuning.EdgeDistanceTolerancePixels = _structureEdgeTolerance.Value;
            if (double.IsFinite(_structureTopCandidates.Value))
            {
                tuning.TopCandidateCount =
                    (int)Math.Round(_structureTopCandidates.Value);
            }
            if (double.IsFinite(_structureBudget.Value))
            {
                tuning.StructureFallbackBudgetMilliseconds =
                    (int)Math.Round(_structureBudget.Value);
            }
            await _runtime.SetStructureRegistrationTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ElevationButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "管理员重启",
            Content = "将请求管理员权限启动新的 Identity Vision Bridge。新进程启动成功后才会关闭当前窗口。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (_runtime.TryRestartElevated(out var failureReason))
        {
            ((App)Application.Current).MainWindow.Close();
            return;
        }
        _status.Text = failureReason;
    }
}
