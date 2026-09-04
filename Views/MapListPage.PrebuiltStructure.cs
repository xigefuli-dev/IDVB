using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private void AttachClassEditMenu(Button button)
    {
        var menu = new MenuFlyout();
        var properties = new MenuFlyoutItem { Text = "地图类属性" };
        properties.Click += async (_, _) => await ShowClassPropertiesDialogAsync();
        var generate = new MenuFlyoutItem { Text = "预生成线图算法" };
        generate.Click += async (_, _) => await PickAndGeneratePrebuiltStructureAsync();
        var preview = new MenuFlyoutItem
        {
            Text = "预览预制线图",
            IsEnabled = CurrentClassHasCompletePrebuiltStructureLines()
        };
        preview.Click += (_, _) => ShowPrebuiltStructurePreview();
        menu.Items.Add(properties);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(generate);
        menu.Items.Add(preview);
        button.Flyout = menu;
    }

    private bool CurrentClassHasCompletePrebuiltStructureLines()
    {
        var maps = _loadedMaps
            .Where(map => string.Equals(map.Class, _selectedClass, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return maps.Length > 0 && maps.All(_repository.HasCompletePrebuiltStructureLines);
    }

    private async Task PickAndGeneratePrebuiltStructureAsync()
    {
        if (_isPackageOperation || string.IsNullOrWhiteSpace(_selectedClass))
            return;
        string? path;
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "加载算法",
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".idva");
            var result = await picker.PickSingleFileAsync();
            path = result?.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件选择器", exception.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
            return;
        await GeneratePrebuiltStructureAsync(path);
    }

    private async Task GeneratePrebuiltStructureAsync(string path)
    {
        using var cancellation = new CancellationTokenSource();
        var label = new TextBlock
        {
            Text = "正在验证 IDVA 算法包…",
            TextWrapping = TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            Text = "0 / 0",
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        };
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsIndeterminate = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(label);
        content.Children.Add(bar);
        content.Children.Add(detail);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "生成预制线图",
            Content = content,
            CloseButtonText = "取消"
        };
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
        _ = dialog.ShowAsync();
        SetClassEditBusy(true);
        try
        {
            var progress = new Progress<PrebuiltStructureBatchProgress>(value =>
            {
                bar.Maximum = Math.Max(1d, value.TotalWork);
                bar.Value = Math.Clamp(value.CompletedWork, 0d, bar.Maximum);
                label.Text = $"{value.MapName} · {value.FloorName}\n{DisplayStage(value.StageName)}";
                detail.Text = $"楼层 {value.CompletedFloors} / {value.TotalFloors} · {bar.Value / bar.Maximum:P0}";
            });
            var result = await _repository.GeneratePrebuiltStructureLinesAsync(
                _selectedClass,
                path,
                progress,
                cancellation.Token);
            dialog.Hide();
            await ShowListAsync();
            await ShowMessageAsync(
                "预制线图已生成",
                $"已使用“{result.AlgorithmDisplayName}”处理 {result.MapCount} 张地图、{result.FloorCount} 个楼层。\n算法：{result.AlgorithmId}");
            ShowPrebuiltStructurePreview();
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
            await ShowMessageAsync("已取消", "没有登记未完成的预制线图。");
        }
        catch (Exception exception)
        {
            dialog.Hide();
            await ShowMessageAsync("预制线图生成失败", exception.Message);
        }
        finally
        {
            SetClassEditBusy(false);
        }
    }

    private void ShowPrebuiltStructurePreview()
    {
        var maps = _loadedMaps
            .Where(map => string.Equals(map.Class, _selectedClass, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ToArray();
        if (maps.Length == 0 || !maps.All(_repository.HasCompletePrebuiltStructureLines))
            return;
        var title = new TextBlock
        {
            Text = $"{_selectedClass} · 预制线图",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var previous = CreateSecondaryButton("上一张");
        var next = CreateSecondaryButton("下一张");
        var back = CreateSecondaryButton("返回地图列表");
        back.Click += async (_, _) => await ShowListAsync();
        var flip = new FlipView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        foreach (var map in maps)
            flip.Items.Add(CreatePrebuiltMapPreview(map));
        flip.SelectedIndex = 0;
        void UpdatePageState()
        {
            var index = Math.Max(0, flip.SelectedIndex);
            pageLabel.Text = $"{index + 1} / {maps.Length}";
            previous.IsEnabled = index > 0;
            next.IsEnabled = index + 1 < maps.Length;
        }
        previous.Click += (_, _) => flip.SelectedIndex = Math.Max(0, flip.SelectedIndex - 1);
        next.Click += (_, _) => flip.SelectedIndex = Math.Min(maps.Length - 1, flip.SelectedIndex + 1);
        flip.SelectionChanged += (_, _) => UpdatePageState();
        var header = new Grid { ColumnSpacing = 10, Margin = new Thickness(36, 24, 36, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(back);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(pageLabel, 2);
        header.Children.Add(pageLabel);
        Grid.SetColumn(previous, 3);
        header.Children.Add(previous);
        Grid.SetColumn(next, 4);
        header.Children.Add(next);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(flip, 1);
        root.Children.Add(flip);
        _workflowHost.Content = root;
        UpdatePageState();
    }

    private FrameworkElement CreatePrebuiltMapPreview(MapRecord map)
    {
        var content = new StackPanel { Spacing = 20, Margin = new Thickness(36, 0, 36, 36) };
        content.Children.Add(new TextBlock
        {
            Text = map.DisplayName,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        foreach (var floor in MapFloorRules.GetOrderedFloors(map))
        {
            var asset = floor.PrebuiltStructureLine!;
            content.Children.Add(new TextBlock
            {
                Text = $"{floor.DisplayName} · {asset.Width} × {asset.Height} · {asset.AlgorithmId}",
                FontSize = 16
            });
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 18, 18)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new Image
                {
                    Source = new BitmapImage(new Uri(_repository.GetPrebuiltStructureLinePath(map, floor.Key))),
                    Stretch = Stretch.Uniform,
                    MaxHeight = 620,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
        }
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static string DisplayStage(string stage) => stage switch
    {
        "color_classification" => "颜色分类",
        "class_conflict_resolution" => "类别消歧",
        "morph_open" => "形态学开运算",
        "remove_small_components" => "过滤小连通域",
        "morph_close" => "形态学闭运算",
        "fill_holes" => "填充孔洞",
        "directional_bridge" => "定向桥接",
        "fill_small_holes" => "修复小孔洞",
        "contours" or "room_contours" or "corridor_contours" => "提取轮廓",
        "draw_edges" => "绘制结构线",
        "已完成" => "已完成",
        _ => stage
    };
}
