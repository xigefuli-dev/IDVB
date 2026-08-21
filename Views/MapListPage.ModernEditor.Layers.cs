using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private sealed record ModernLayerItem(
        string Key,
        string Label,
        string Glyph,
        EditorSelection? Selection = null,
        Color? Accent = null);

    private void RefreshModernLayerList()
    {
        if (_modernLayerList is null || _draft is null)
            return;
        _modernLayerList.Children.Clear();
        var profile = GetActiveFloorProfile();
        _modernLayerList.Children.Add(CreateModernLayerGroup("image", "地图图片",
        [
            new ModernLayerItem("image", GetModernFloorDisplayName(_activeFloorKey), "\uEB9F")
        ]));

        var graphicItems = new List<ModernLayerItem>();
        var textNumber = 0;
        var lineNumber = 0;
        var rectangleNumber = 0;
        foreach (var annotation in profile.Annotations)
        {
            var (label, glyph) = annotation.Type switch
            {
                MapAnnotationType.Text => ($"文字 {++textNumber}  {annotation.Text}", "\uE8D2"),
                MapAnnotationType.Line => ($"直线 {++lineNumber}", "\uE8A1"),
                _ => ($"矩形 {++rectangleNumber}", "\uE7FB")
            };
            graphicItems.Add(new ModernLayerItem(
                ModernAnnotationKey(annotation.Id),
                label,
                glyph,
                new EditorSelection(EditorSelectionKind.Annotation, annotation.Id),
                ParseEditorColor(annotation.EffectiveColorHex)));
        }
        _modernLayerList.Children.Add(CreateModernLayerGroup("graphics", "图形元素", graphicItems));

        var specialItems = new List<ModernLayerItem>();
        if (profile.RecognitionRegion is not null)
        {
            specialItems.Add(new ModernLayerItem(
                "crop",
                "画布裁剪",
                "\uE7A8",
                new EditorSelection(EditorSelectionKind.Crop),
                RecognitionRegionRed));
        }
        var anchorNumber = 0;
        foreach (var anchor in profile.Anchors)
        {
            var isGate = anchor.Key is "main-entrance" or "side-entrance";
            var label = isGate ? anchor.DisplayName : $"{anchor.DisplayName} {++anchorNumber}";
            specialItems.Add(new ModernLayerItem(
                ModernAnchorKey(anchor.Id),
                label,
                isGate ? "\uE839" : "\uE707",
                new EditorSelection(EditorSelectionKind.Anchor, anchor.Id),
                GetAnchorColor(anchor)));
        }
        var concealNumber = 0;
        foreach (var layer in profile.BackgroundLayers.Where(layer => layer.IsValid))
        {
            specialItems.Add(new ModernLayerItem(
                ModernBackgroundKey(layer.Id),
                $"遮瑕 {++concealNumber}",
                "\uE74A",
                new EditorSelection(EditorSelectionKind.Background, layer.Id),
                Color.FromArgb(255, 245, 86, 44)));
        }
        _modernLayerList.Children.Add(CreateModernLayerGroup("special", "特殊元素", specialItems));
    }

    private Expander CreateModernLayerGroup(string key, string title, IReadOnlyList<ModernLayerItem> items)
    {
        var isVisible = !_hiddenEditorGroups.Contains(key);
        var header = new Grid { MinHeight = 40, ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = new SolidColorBrush(EditorText),
            VerticalAlignment = VerticalAlignment.Center
        });
        var eye = CreateModernEyeButton(isVisible);
        eye.Click += (_, _) =>
        {
            ToggleModernGroupVisibility(key);
        };
        Grid.SetColumn(eye, 1);
        header.Children.Add(eye);

        var rows = new StackPanel { Spacing = 1 };
        foreach (var item in items)
            rows.Children.Add(CreateModernLayerRow(key, item));
        if (items.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "暂无元素",
                Margin = new Thickness(35, 7, 8, 10),
                FontSize = 11,
                Foreground = new SolidColorBrush(EditorMuted)
            });
        }

        if (_modernLayerGroups.TryGetValue(key, out var existing))
        {
            existing.Header = header;
            existing.Content = rows;
            return existing;
        }

        var expander = new Expander
        {
            Header = header,
            Content = rows,
            IsExpanded = !_editorGroupExpansion.TryGetValue(key, out var expanded) || expanded,
            Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255))
        };
        expander.Expanding += (_, _) => _editorGroupExpansion[key] = true;
        expander.Collapsed += (_, _) => _editorGroupExpansion[key] = false;
        _modernLayerGroups[key] = expander;
        return expander;
    }

    private Grid CreateModernLayerRow(string groupKey, ModernLayerItem item)
    {
        var selected = item.Selection is not null && Equals(_modernSelection, item.Selection);
        var row = new Grid
        {
            MinHeight = 38,
            Padding = new Thickness(9, 3, 5, 3),
            Background = new SolidColorBrush(selected ? Color.FromArgb(110, 26, 119, 220) : Color.FromArgb(0, 0, 0, 0))
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = item.Glyph,
            FontSize = 14,
            Foreground = new SolidColorBrush(item.Accent ?? EditorMuted),
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new Button
        {
            Content = item.Label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(EditorText),
            FontSize = 12,
            Padding = new Thickness(3),
            IsEnabled = item.Selection is not null
        };
        label.Click += (_, _) =>
        {
            if (item.Selection is null || !IsModernItemVisible(groupKey, item.Key))
                return;
            _modernToolState.Select(MapEditorTool.Select);
            _modernSelection = item.Selection;
            RefreshModernToolVisuals();
            RenderModernEditor();
            RefreshModernLayerList();
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var eye = CreateModernEyeButton(!_hiddenEditorItems.Contains(item.Key));
        eye.Click += (_, _) => ToggleModernItemVisibility(groupKey, item.Key, item.Selection);
        Grid.SetColumn(eye, 2);
        row.Children.Add(eye);
        return row;
    }

    private static Button CreateModernEyeButton(bool visible) => new()
    {
        Content = new FontIcon { Glyph = visible ? "\uE890" : "\uED1A", FontSize = 14 },
        Width = 34,
        Height = 30,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(visible ? EditorText : EditorMuted)
    };

    private void ToggleModernGroupVisibility(string groupKey)
    {
        if (!_hiddenEditorGroups.Add(groupKey))
            _hiddenEditorGroups.Remove(groupKey);
        if (_hiddenEditorGroups.Contains(groupKey) && SelectionBelongsToModernGroup(_modernSelection, groupKey))
            _modernSelection = null;
        if (groupKey == "image" && _modernImage is not null)
            _modernImage.Visibility = IsModernItemVisible("image", "image") ? Visibility.Visible : Visibility.Collapsed;
        RenderModernEditor();
        RefreshModernLayerList();
    }

    private void ToggleModernItemVisibility(string groupKey, string itemKey, EditorSelection? selection)
    {
        if (!_hiddenEditorItems.Add(itemKey))
            _hiddenEditorItems.Remove(itemKey);
        if (_hiddenEditorItems.Contains(itemKey) && selection is not null && Equals(selection, _modernSelection))
            _modernSelection = null;
        if (itemKey == "image" && _modernImage is not null)
            _modernImage.Visibility = IsModernItemVisible(groupKey, itemKey) ? Visibility.Visible : Visibility.Collapsed;
        RenderModernEditor();
        RefreshModernLayerList();
    }

    private bool SelectionBelongsToModernGroup(EditorSelection? selection, string groupKey) => selection?.Kind switch
    {
        EditorSelectionKind.Annotation => groupKey == "graphics",
        EditorSelectionKind.Anchor or EditorSelectionKind.Crop or EditorSelectionKind.Background => groupKey == "special",
        _ => false
    };

    private bool IsModernItemVisible(string groupKey, string itemKey) =>
        !_hiddenEditorGroups.Contains(groupKey) && !_hiddenEditorItems.Contains(itemKey);

    private static string ModernAnnotationKey(Guid id) => $"annotation:{id:N}";
    private static string ModernAnchorKey(Guid id) => $"anchor:{id:N}";
    private static string ModernBackgroundKey(Guid id) => $"background:{id:N}";
}
