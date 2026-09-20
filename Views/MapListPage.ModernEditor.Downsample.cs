using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenCvSharp;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private async Task ShowModernDownsampleDialogAsync()
    {
        if (_draft is null)
            return;

        var floorItems = new List<(string Key, string DisplayName, string Path, int Width, int Height)>();
        foreach (var floor in _draft.Floors.OrderBy(f => f.SortOrder))
        {
            var path = GetFloorImagePath(floor.Key);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    using var img = System.Drawing.Image.FromFile(path);
                    floorItems.Add((floor.Key, floor.DisplayName, path, img.Width, img.Height));
                }
                catch
                {
                    // Ignore unreadable image
                }
            }
        }

        if (floorItems.Count == 0)
        {
            await ShowMessageAsync("无法降采样", "当前地图没有检测到可用的楼层原图。");
            return;
        }

        var panel = new StackPanel { Spacing = 14, Width = 460 };

        // 警告信息卡片
        var warningCard = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(45, 255, 68, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 68, 68)),
            BorderThickness = new Thickness(1)
        };
        var warningContent = new StackPanel { Spacing = 4 };
        warningContent.Children.Add(new TextBlock
        {
            Text = "⚠ 警告：直接修改原图 · 不可撤销",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 120, 120))
        });
        warningContent.Children.Add(new TextBlock
        {
            Text = "降采样操作将直接对磁盘上的原图文件进行下采样覆盖，并同步缩放遮瑕笔刷尺寸。此操作针对当前地图系列的全部楼层，且无法撤销！",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(EditorText)
        });
        warningCard.Child = warningContent;
        panel.Children.Add(warningCard);

        // 倍率选择
        var factorPanel = new StackPanel { Spacing = 6 };
        factorPanel.Children.Add(new TextBlock
        {
            Text = "降采样倍率",
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });
        var factorCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "降低 2x (推荐)", "降低 3x", "降低 4x" },
            SelectedIndex = 0
        };
        factorPanel.Children.Add(factorCombo);
        panel.Children.Add(factorPanel);

        // 预览列表标题
        panel.Children.Add(new TextBlock
        {
            Text = "各楼层分辨率预期变化：",
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });

        var previewPanel = new StackPanel { Spacing = 6 };
        panel.Children.Add(previewPanel);

        void RefreshPreview()
        {
            previewPanel.Children.Clear();
            var factor = factorCombo.SelectedIndex switch
            {
                1 => 3,
                2 => 4,
                _ => 2
            };

            foreach (var item in floorItems)
            {
                var targetW = Math.Max(1, item.Width / factor);
                var targetH = Math.Max(1, item.Height / factor);

                var row = new Grid { Padding = new Thickness(8, 6, 8, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameText = new TextBlock
                {
                    Text = item.DisplayName,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(EditorText),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var changeText = new TextBlock
                {
                    Text = $"{item.Width} × {item.Height}   →   {targetW} × {targetH} px",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 187, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(nameText);
                Grid.SetColumn(changeText, 1);
                row.Children.Add(changeText);

                var itemBorder = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    Child = row
                };
                previewPanel.Children.Add(itemBorder);
            }
        }

        factorCombo.SelectionChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "原图降采样（整套地图系列）",
            Content = panel,
            PrimaryButtonText = "确认降采样",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var selectedFactor = factorCombo.SelectedIndex switch
        {
            1 => 3,
            2 => 4,
            _ => 2
        };

        await ExecuteDownsampleMapSeriesAsync(selectedFactor, floorItems);
    }

    private async Task ExecuteDownsampleMapSeriesAsync(
        int factor,
        IReadOnlyList<(string Key, string DisplayName, string Path, int Width, int Height)> floorItems)
    {
        if (_draft is null)
            return;

        SetModernStatus($"正在执行 {factor}x 降采样...", false);

        // 1. 彻底清空现有位图加载，释放文件句柄
        if (_modernImage is not null)
            _modernImage.Source = null;
        foreach (var entry in _modernFloorBitmaps.Values)
        {
            entry.Bitmap.ImageOpened -= entry.OpenedHandler;
            entry.Bitmap.UriSource = null;
        }
        _modernFloorBitmaps.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            // 2. 后台线程执行图像物理降采样并覆盖写回原文件
            await Task.Run(() =>
            {
                var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in floorItems)
                {
                    if (processedPaths.Add(item.Path))
                        DownsampleImageFileOnDisk(item.Path, factor);
                }

                // 同步处理识别源图与预览图（若存在且不一致）
                foreach (var floor in _draft.Floors)
                {
                    if (_draft.FloorRecognitionSourcePaths.TryGetValue(floor.Key, out var recogPath)
                        && File.Exists(recogPath)
                        && processedPaths.Add(recogPath))
                    {
                        DownsampleImageFileOnDisk(recogPath, factor);
                    }
                    if (_draft.FloorPreviewPaths.TryGetValue(floor.Key, out var prevPath)
                        && File.Exists(prevPath)
                        && processedPaths.Add(prevPath))
                    {
                        DownsampleImageFileOnDisk(prevPath, factor);
                    }
                }
            });

            // 3. 缩放遮瑕笔刷
            MapRepository.ScaleBackgroundBrushes(_draft.Recognition, 0, factor);

            // 4. 若为已有地图，同步持久化到 Repository 与刷新运行时缓存
            if (_draft.Id is { } mapId)
            {
                await _repository.SaveAsync(_draft);
                if (!App.IsSafeMode)
                    await App.Session.RefreshMapCacheAsync(mapId);
            }

            // 5. 重新加载当前楼层底图、刷新视口和分辨率
            SwitchModernFloor(_activeFloorKey, fitWhenLoaded: true);
            SetModernStatus($"已成功将当前地图全楼层降采样 {factor}x。", false);
        }
        catch (Exception ex)
        {
            SetModernStatus($"降采样失败：{ex.Message}", true);
            await ShowMessageAsync("降采样失败", ex.Message);
            SwitchModernFloor(_activeFloorKey, fitWhenLoaded: true);
        }
    }

    private static void DownsampleImageFileOnDisk(string filePath, int factor)
    {
        if (!File.Exists(filePath) || factor <= 1)
            return;

        var rawBytes = File.ReadAllBytes(filePath);
        using var source = Cv2.ImDecode(rawBytes, ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidOperationException($"无法读取图像：{Path.GetFileName(filePath)}");

        var targetWidth = Math.Max(1, source.Width / factor);
        var targetHeight = Math.Max(1, source.Height / factor);

        using var resized = new Mat();
        Cv2.Resize(source, resized, new OpenCvSharp.Size(targetWidth, targetHeight), 0, 0, InterpolationFlags.Area);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!Cv2.ImEncode(ext, resized, out var encodedBytes))
            throw new InvalidOperationException($"无法编码图像：{Path.GetFileName(filePath)}");

        var tempPath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? AppDataPaths.RootDirectory,
            $".downsample-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tempPath, encodedBytes);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
