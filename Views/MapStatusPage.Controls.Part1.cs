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
