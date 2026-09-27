using IDVBuff.Features.Maps;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void SelectModernTool(MapEditorTool tool, Button? placementTarget = null)
    {
        if (_modernToolState.ActiveTool == tool
            && tool is MapEditorTool.Text or MapEditorTool.Line or MapEditorTool.Conceal)
        {
            if (placementTarget is not null)
            {
                if (tool == MapEditorTool.Text)
                    ShowModernTextProperties(placementTarget);
                else if (tool == MapEditorTool.Line)
                    ShowModernLineProperties(placementTarget);
                else
                    ShowModernConcealProperties(placementTarget);
            }
            return;
        }
        EndModernContinuousLine();
        _modernToolState.ActiveFloorKey = _activeFloorKey;
        _modernToolState.Select(tool);
        CancelModernInteraction(restoreGeometry: true);
        _modernSelection = null;
        _modernSelectedAnnotationIds.Clear();
        _modernAnnotationSelectionAnchorId = null;
        SetModernStatus(tool == MapEditorTool.Gate
            ? _modernToolState.UsesPrimaryGatePair
                ? "请先点击标记正门。"
                : "请点击标记次要门特征。"
            : ModernToolHint(tool));
        RefreshModernToolVisuals();
        RenderModernEditor();
        RefreshModernLayerList();
    }

    private void EndModernContinuousLine() => _modernContinuousLineStart = null;

    private void UpdateModernFloorResolution(int width, int height)
    {
        if (_modernFloorResolutionText is null)
            return;
        if (width > 0 && height > 0)
        {
            _modernFloorResolutionText.Text = $"原图 {width} \u00D7 {height}";
            if (_modernFloorResolutionContainer is not null)
            {
                ToolTipService.SetToolTip(
                    _modernFloorResolutionContainer,
                    $"当前楼层原图物理分辨率：{width} \u00D7 {height}");
            }
        }
        else
        {
            _modernFloorResolutionText.Text = "原图 -- \u00D7 --";
            if (_modernFloorResolutionContainer is not null)
            {
                ToolTipService.SetToolTip(_modernFloorResolutionContainer, "当前楼层未加载原图");
            }
        }
    }
    private async Task LoadEditorPreferencesAsync()
    {
        if (_recentColorsLoaded)
            return;
        _editorPreferenceState = await _editorPreferences.LoadAsync();
        _recentAnnotationColors.Replace(_editorPreferenceState.RecentColors.AsEnumerable().Reverse());
        _recentColorsLoaded = true;
    }

    private async Task RememberEditorColorAsync(string color)
    {
        if (!_recentAnnotationColors.Use(color))
            return;
        try
        {
            _editorPreferenceState.RecentColors = _recentAnnotationColors.Colors.ToList();
            await _editorPreferences.SaveAsync(_editorPreferenceState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save map editor colors: {exception.Message}");
        }
    }

    private void ShowModernColorPicker(Button placementTarget)
    {
        var panel = new StackPanel { Spacing = 8, Width = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = "最近使用",
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });
        var recents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, MinHeight = 34 };
        foreach (var recent in _recentAnnotationColors.Colors)
        {
            var captured = recent;
            var swatch = new Button
            {
                Width = 31,
                Height = 31,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(ParseEditorColor(recent)),
                BorderBrush = new SolidColorBrush(EditorText),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16)
            };
            swatch.Click += (_, _) => ApplySelectedEditorColor(captured);
            recents.Children.Add(swatch);
        }
        panel.Children.Add(recents);
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            Color = ParseEditorColor(_currentAnnotationColor)
        };
        picker.ColorChanged += (_, args) => ApplySelectedEditorColor(ToEditorColorHex(args.NewColor));
        panel.Children.Add(picker);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.ShowAt(placementTarget);
    }

    private void ApplySelectedEditorColor(string color)
    {
        if (!MapAnnotationColor.TryNormalize(color, out var normalized))
            return;
        _currentAnnotationColor = normalized;
        if (_draft is not null && _modernSelectedAnnotationIds.Count > 0)
        {
            foreach (var annotation in GetActiveFloorProfile().Annotations)
            {
                if (!_modernSelectedAnnotationIds.Contains(annotation.Id))
                    continue;
                annotation.ColorHex = normalized;
                annotation.ColorIndex = MapAnnotationColor.ToLegacyIndex(normalized);
            }
            RenderModernEditor();
            RefreshModernLayerList();
            SetModernStatus($"已更改 {_modernSelectedAnnotationIds.Count} 个图形元素的颜色。");
        }
        if (_modernColorIndicator is not null)
            _modernColorIndicator.Background = new SolidColorBrush(ParseEditorColor(normalized));
    }

    private async Task SaveEditorPreferencesAsync()
    {
        try
        {
            _editorPreferenceState.RecentColors = _recentAnnotationColors.Colors.ToList();
            await _editorPreferences.SaveAsync(_editorPreferenceState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save map editor preferences: {exception.Message}");
        }
    }

}
