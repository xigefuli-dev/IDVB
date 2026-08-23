using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    private void MapOpacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var percentage = Math.Clamp(args.NewValue, 0d, 100d);
        _mapOpacityValue.Text = $"当前：{percentage:F0}%";
        QueueSliderSave(_mapOpacitySave, percentage / 100d, _runtime.SetMapOpacityAsync);
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

    private async void ShowLineAnnotations_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowLineAnnotationsAsync(_showLineAnnotationsToggle.IsOn); }
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

    private async void ShowLineAnnotationsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowLineAnnotationsOnMiniMapAsync(_showLineAnnotationsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowFloorOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowFloorOnMiniMapAsync(_showFloorOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private void MiniMapScale_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;

        var percentage = Math.Clamp(args.NewValue, 10d, 100d);
        _miniMapScaleValue.Text = $"当前：{percentage:F0}%";

        _miniMapScaleSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _miniMapScaleSaveCancellation = cancellation;
        _ = SaveMiniMapScaleAfterDragAsync(percentage / 100d, cancellation);
    }

    private async Task SaveMiniMapScaleAfterDragAsync(
        double scale,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await _runtime.SetMiniMapScaleAsync(scale);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
        finally
        {
            if (ReferenceEquals(_miniMapScaleSaveCancellation, cancellation))
                _miniMapScaleSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private void MiniMapOpacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var percentage = Math.Clamp(args.NewValue, 0d, 100d);
        _miniMapOpacityValue.Text = $"当前：{percentage:F0}%";
        QueueSliderSave(_miniMapOpacitySave, percentage / 100d, _runtime.SetMiniMapOpacityAsync);
    }

    private void MiniMapOffsetX_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var offset = Math.Clamp(args.NewValue, -500d, 500d);
        _miniMapOffsetXValue.Text = $"当前：{offset:F0} px";
        QueueSliderSave(_miniMapOffsetXSave, offset, _runtime.SetMiniMapOffsetXAsync);
    }

    private void MiniMapOffsetY_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var offset = Math.Clamp(args.NewValue, -500d, 500d);
        _miniMapOffsetYValue.Text = $"当前：{offset:F0} px";
        QueueSliderSave(_miniMapOffsetYSave, offset, _runtime.SetMiniMapOffsetYAsync);
    }

    private void StatusOpacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var percentage = Math.Clamp(args.NewValue, 0d, 100d);
        _statusOpacityValue.Text = $"当前：{percentage:F0}%";
        QueueSliderSave(_statusOpacitySave, percentage / 100d, _runtime.SetStatusOpacityAsync);
    }

    private void StatusOffsetX_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var offset = Math.Clamp(args.NewValue, -500d, 500d);
        _statusOffsetXValue.Text = $"当前：{offset:F0} px";
        QueueSliderSave(_statusOffsetXSave, offset, _runtime.SetStatusOffsetXAsync);
    }

    private void StatusOffsetY_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        var offset = Math.Clamp(args.NewValue, -500d, 500d);
        _statusOffsetYValue.Text = $"当前：{offset:F0} px";
        QueueSliderSave(_statusOffsetYSave, offset, _runtime.SetStatusOffsetYAsync);
    }

    private void QueueSliderSave(
        SliderSaveState saveState,
        double value,
        Func<double, Task> save)
    {
        saveState.Cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        saveState.Cancellation = cancellation;
        _ = SaveSliderValueAfterDragAsync(saveState, value, save, cancellation);
    }

    private async Task SaveSliderValueAfterDragAsync(
        SliderSaveState saveState,
        double value,
        Func<double, Task> save,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await save(value);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
        finally
        {
            if (ReferenceEquals(saveState.Cancellation, cancellation))
                saveState.Cancellation = null;
            cancellation.Dispose();
        }
    }

    private async void CollectLogsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            if (!_collectLogsToggle.IsOn
                && !await ConfirmDataCleanupAsync(
                    "关闭日志收集",
                    "关闭后会清理已收集的日志数据和临时文件，此操作不可恢复。"))
            {
                RestoreToggle(_collectLogsToggle);
                return;
            }

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
            if (!_collectResearchToggle.IsOn
                && !await ConfirmDataCleanupAsync(
                    "关闭算法研究采集",
                    "关闭后会清理已采集的算法研究样本和临时文件，此操作不可恢复。"))
            {
                RestoreToggle(_collectResearchToggle);
                return;
            }

            await _runtime.SetCollectAlignmentResearchDataAsync(
                _collectResearchToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task<bool> ConfirmDataCleanupAsync(string title, string content)
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content + "\n\n是否继续关闭并清理？",
            PrimaryButtonText = "关闭并清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await prompt.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RestoreToggle(ToggleSwitch toggle)
    {
        _refreshing = true;
        try
        {
            toggle.IsOn = true;
        }
        finally
        {
            _refreshing = false;
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

    private async void BackgroundScan_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetBackgroundScanEnabledAsync(
                _backgroundScanToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

}
