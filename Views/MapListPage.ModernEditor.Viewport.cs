using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void ModernEditorRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateModernResponsiveLayout(e.NewSize.Width);

    private void ModernParentViewport_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyModernViewportSize(e.NewSize.Width, e.NewSize.Height);

    private void ApplyModernViewportSize(double availableWidth, double availableHeight)
    {
        if (_modernEditorRoot is null)
            return;
        var width = Math.Max(1, availableWidth);
        var height = Math.Max(1, availableHeight);
        if (Math.Abs(_modernEditorRoot.Width - width) > .5)
            _modernEditorRoot.Width = width;
        if (Math.Abs(_modernEditorRoot.Height - height) > .5)
            _modernEditorRoot.Height = height;
        UpdateModernResponsiveLayout(width);
        DispatcherQueue.TryEnqueue(FitModernCanvas);
    }

    private void UpdateModernResponsiveLayout(double width)
    {
        var narrow = width < 1050;
        _modernLayersAreDrawer = narrow;
        if (_modernLayerDrawerButton is not null)
            _modernLayerDrawerButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        if (_modernFocusMode)
            return;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = narrow && !_modernLayerDrawerOpen ? new GridLength(0) : new GridLength(286);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = narrow && !_modernLayerDrawerOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleModernLayerDrawer()
    {
        _modernLayerDrawerOpen = !_modernLayerDrawerOpen;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = _modernLayerDrawerOpen ? new GridLength(286) : new GridLength(0);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = _modernLayerDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(FitModernCanvas);
    }

    private void ToggleModernFocusMode()
    {
        _modernFocusMode = !_modernFocusMode;
        if (_modernEditorHeader is not null)
            _modernEditorHeader.Visibility = _modernFocusMode ? Visibility.Collapsed : Visibility.Visible;
        var showLayerPane = !_modernFocusMode && (!_modernLayersAreDrawer || _modernLayerDrawerOpen);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = showLayerPane ? Visibility.Visible : Visibility.Collapsed;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = showLayerPane ? new GridLength(286) : new GridLength(0);
        SetModernStatus(_modernFocusMode ? "已进入专注模式，按 Esc 退出。" : "已退出专注模式。", false);
        DispatcherQueue.TryEnqueue(FitModernCanvas);
    }

    private void FitModernCanvas()
    {
        if (_modernViewport is null || _modernScene is null || _modernScene.Width <= 0 || _modernScene.Height <= 0)
            return;
        var availableWidth = Math.Max(1, _modernViewport.ActualWidth - 24);
        var availableHeight = Math.Max(1, _modernViewport.ActualHeight - 24);
        var zoom = (float)Math.Clamp(Math.Min(availableWidth / _modernScene.Width, availableHeight / _modernScene.Height), .1, 8);
        _modernViewport.ChangeView(0, 0, zoom, true);
    }

    private void ChangeModernZoom(float multiplier)
    {
        if (_modernViewport is null)
            return;
        var zoom = Math.Clamp(_modernViewport.ZoomFactor * multiplier, .1f, 8f);
        var centerX = _modernViewport.HorizontalOffset + _modernViewport.ViewportWidth / 2;
        var centerY = _modernViewport.VerticalOffset + _modernViewport.ViewportHeight / 2;
        var scale = zoom / _modernViewport.ZoomFactor;
        _modernViewport.ChangeView(
            Math.Max(0, centerX * scale - _modernViewport.ViewportWidth / 2),
            Math.Max(0, centerY * scale - _modernViewport.ViewportHeight / 2),
            zoom,
            false);
    }

    private void UpdateModernZoomText()
    {
        if (_modernZoomText is not null && _modernViewport is not null)
            _modernZoomText.Text = $"{Math.Round(_modernViewport.ZoomFactor * 100):0}%";
    }

    private void SetModernStatus(string text, bool isError = false)
    {
        if (_modernStatusText is null)
            return;
        _modernStatusText.Text = text;
        _modernStatusText.Foreground = new SolidColorBrush(isError ? Color.FromArgb(255, 255, 125, 104) : EditorMuted);
    }

    private static Color ParseEditorColor(string? value)
    {
        if (!MapAnnotationColor.TryNormalize(value, out var normalized))
            normalized = MapAnnotationColor.Default;
        return Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static string ToEditorColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }
}
