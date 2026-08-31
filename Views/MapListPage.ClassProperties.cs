using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private async Task ShowClassPropertiesDialogAsync()
    {
        if (_isPackageOperation || string.IsNullOrWhiteSpace(_selectedClass))
            return;

        var current = _classProperties.TryGetValue(_selectedClass, out var value)
            ? value.Clone()
            : new MapClassProperties();
        var toggle = new ToggleSwitch
        {
            Header = "去除背景",
            IsOn = current.RemoveBackground,
            OffContent = "关闭",
            OnContent = "开启",
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };
        var intensityValue = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity).ToString(),
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        };
        var intensitySlider = new Slider
        {
            Header = "去除背景强度",
            Minimum = MapBackgroundProcessor.MinBackgroundRemovalIntensity,
            Maximum = MapBackgroundProcessor.MaxBackgroundRemovalIntensity,
            Value = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity),
            SmallChange = 1,
            StepFrequency = 1,
            IsEnabled = toggle.IsOn,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        intensitySlider.ValueChanged += (_, args) =>
            intensityValue.Text = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                (int)Math.Round(args.NewValue)).ToString();
        toggle.Toggled += (_, _) => intensitySlider.IsEnabled = toggle.IsOn;
        var classMaps = _loadedMaps
            .Where(map => string.Equals(
                map.Class,
                _selectedClass,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var scanFloorCombo = new ComboBox
        {
            Header = "用于扫描的楼层",
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "请选择楼层 ID"
        };
        var configuredScanFloor = MapScanFloorRules.NormalizeFloorIdentity(
            current.ScanFloorKey);
        var compatibilityPrimaryFloor = classMaps.Length == 0
            ? null
            : MapScanFloorRules.NormalizeFloorIdentity(
                MapFloorRules.GetPrimaryFloorKey(classMaps[0]));
        foreach (var option in MapScanFloorRules.BuildOptions(classMaps))
        {
            var isDefaultPrimary = classMaps.All(map =>
                MapScanFloorRules.ResolveFloorKey(map, option.FloorIdentity)
                    is { } floorKey
                && MapScanFloorRules.IsPrimaryFloor(map, floorKey));
            var item = new ComboBoxItem
            {
                Content = option.IsEligible
                    ? $"{option.DisplayName}（{option.FloorIdentity}）· {(isDefaultPrimary ? "默认主楼层" : "其他楼层")}"
                    : $"{option.DisplayName}（{option.FloorIdentity}，不可用）",
                Tag = option.FloorIdentity,
                IsEnabled = option.IsEligible
            };
            if (!option.IsEligible)
                ToolTipService.SetToolTip(item, option.FailureReason);
            scanFloorCombo.Items.Add(item);
            if (string.Equals(
                    option.FloorIdentity,
                    configuredScanFloor,
                    StringComparison.Ordinal))
            {
                scanFloorCombo.SelectedItem = item;
            }
            else if (scanFloorCombo.SelectedItem is null
                && configuredScanFloor is null
                && option.IsEligible
                && string.Equals(
                    option.FloorIdentity,
                    compatibilityPrimaryFloor,
                    StringComparison.Ordinal))
            {
                scanFloorCombo.SelectedItem = item;
            }
        }
        scanFloorCombo.SelectedItem ??= scanFloorCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.IsEnabled);
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = $"地图类：{_selectedClass}",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "该设置会重建此类的全部地图和楼层。人工遮瑕层始终保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        content.Children.Add(toggle);
        var intensityPanel = new Grid { ColumnSpacing = 12 };
        intensityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        intensityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        intensityPanel.Children.Add(intensitySlider);
        Grid.SetColumn(intensityValue, 1);
        intensityPanel.Children.Add(intensityValue);
        content.Children.Add(intensityPanel);
        content.Children.Add(new TextBlock
        {
            Text = "数值越高，越宽的颜色范围会被认定为背景。此强度仅保存在本机地图类设置中，不会写入 IDVM 数据包。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        content.Children.Add(scanFloorCombo);
        content.Children.Add(new TextBlock
        {
            Text = "默认主楼层使用大门与侧门；其他楼层必须标记可选的“次要门特征”。楼层 ID 忽略大小写，并且必须覆盖此地图类的每张地图。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "地图类编辑",
            Content = content,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = scanFloorCombo.SelectedItem
                is ComboBoxItem { IsEnabled: true }
        };
        scanFloorCombo.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = scanFloorCombo.SelectedItem
                is ComboBoxItem { IsEnabled: true };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var selectedScanFloor = MapScanFloorRules.NormalizeFloorIdentity(
            (scanFloorCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        var removeBackgroundChanged = toggle.IsOn != current.RemoveBackground;
        var backgroundRemovalIntensity = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
            (int)Math.Round(intensitySlider.Value));
        var backgroundRemovalIntensityChanged = backgroundRemovalIntensity
            != MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity);
        var scanFloorChanged = !string.Equals(
            selectedScanFloor,
            configuredScanFloor,
            StringComparison.Ordinal);
        if (!removeBackgroundChanged && !backgroundRemovalIntensityChanged && !scanFloorChanged)
            return;

        SetClassEditBusy(true);
        try
        {
            if (removeBackgroundChanged || backgroundRemovalIntensityChanged)
            {
                await _repository.SetClassBackgroundRemovalAsync(
                    _selectedClass,
                    toggle.IsOn,
                    backgroundRemovalIntensity);
            }
            if (scanFloorChanged)
            {
                await _repository.SetClassScanFloorAsync(
                    _selectedClass,
                    selectedScanFloor);
            }
            _classProperties = (await _repository.GetCatalogSnapshotAsync()).ClassProperties;
            _selectedMapIds.Clear();
            if (!App.IsSafeMode)
                await App.Session.RefreshMapCacheAsync();
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            var failure = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "地图类更新失败",
                Content = new TextBlock
                {
                    Text = exception.Message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "关闭"
            };
            await failure.ShowAsync();
        }
        finally
        {
            SetClassEditBusy(false);
        }
    }

    private void SetClassEditBusy(bool busy)
    {
        if (_classEditButton is not null)
        {
            _classEditButton.IsEnabled = !busy;
            _classEditButton.Content = busy ? "处理中…" : "地图类编辑";
        }
        if (_classComboBox is not null)
            _classComboBox.IsEnabled = !busy;
        if (_editButton is not null)
            _editButton.IsEnabled = !busy && HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = !busy && HasSelection;
        if (_importButton is not null)
            _importButton.IsEnabled = !busy && !_isPackageOperation;
        if (_publishButton is not null)
            _publishButton.IsEnabled = !busy && !_isPackageOperation && _loadedMaps.Count > 0;
    }
}
