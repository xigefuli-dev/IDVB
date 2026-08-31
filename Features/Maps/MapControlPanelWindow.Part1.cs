using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using IDVBuff.Survey.Domain;
using XamlWindow = Microsoft.UI.Xaml.Window;
using IDVBuff.Core.Contracts;
using WinRT.Interop;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Small interactive match controller. This window is intentionally separate
/// from both the click-through map overlay and the full-screen manual selector.
/// </summary>
public sealed partial class MapControlPanelWindow : IDisposable
{
    private readonly Func<Task>? _correctMap;
    private readonly Button _correctMapButton = new()
    {
        Content = "纠正地图",
        MinHeight = 36,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Visibility = Visibility.Collapsed
    };

    private double ResolveDesiredHeight() => _variantContext is null
        ? 410d
        : Math.Clamp(440d + _variantContext.Options.Count * 72d, 410d, 670d);

    private void RefreshCorrectMapVisibility(MapMatchSnapshot snapshot)
    {
        _correctMapButton.Visibility = snapshot.IsStarted
                && snapshot.Mode == MapRunMode.Normal
                && _correctMap is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void CorrectMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_correctMap is null)
            return;
        SetActionsEnabled(false);
        try
        {
            await _correctMap();
        }
        catch (Exception exception)
        {
            _messageText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private async Task<bool> ConfirmAutomaticMapCacheSaveAsync()
    {
        var xamlRoot = (_window?.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null)
            return false;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "保存本局地图缓存？",
            Content = "将从本局收集的稳定缩放样本中生成地图缓存。"
                + "如果本局对齐结果可能有误，请选择不保存。",
            PrimaryButtonText = "保存并退出",
            CloseButtonText = "不保存并退出",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetActionsEnabled(bool enabled)
    {
        _beginButton.IsEnabled = enabled
            && _pendingClass is not null;
        _endButton.IsEnabled = enabled;
        _correctMapButton.IsEnabled = enabled;
        _surveyModeToggle.IsEnabled = enabled && CanChangeSurveyMode(_snapshot);
    }

    private void QueueMapClassSave(string mapClass)
    {
        var previous = _lastMapClassSaveTask;
        _lastMapClassSaveTask = SaveMapClassAfterAsync(previous, mapClass);
    }

    private async Task SaveMapClassAfterAsync(Task previous, string mapClass)
    {
        try
        {
            await previous;
        }
        catch
        {
            // A failed earlier write must not prevent the latest selection
            // from being persisted.
        }

        try
        {
            await _saveLastSelectedMapClass(mapClass);
        }
        catch (Exception exception)
        {
            // The current in-memory selection remains usable for this match.
            _messageText.Text = $"地图模式记忆保存失败：{exception.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;
        _isVisible = false;
        _window?.Close();
        _window = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
