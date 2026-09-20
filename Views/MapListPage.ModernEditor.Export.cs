using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using OpenCvSharp;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private const int ModernPngMinDimension = 64;
    private const int ModernPngMaxDimension = 16384;
    private const long ModernPngMaxPixelCount = 67_108_864;

    private sealed record ModernPngExportOptions(
        int Width,
        int Height,
        int CompressionLevel,
        Color BackgroundColor);

    private readonly record struct ModernPngPixelRegion(int Left, int Top, int Width, int Height);

    private async Task ShowModernPngExportDialogAsync()
    {
        if (_modernExportInProgress)
            return;
        if (_draft is null || _modernScene is null || _modernCanvas is null
            || _modernCanvas.Width <= 0 || _modernCanvas.Height <= 0)
        {
            await ShowMessageAsync("无法导出 PNG", "当前楼层的地图画布尚未准备完成。");
            return;
        }

        CancelModernInteraction(restoreGeometry: true);
        var canvasWidth = Math.Max(1, (int)Math.Round(_modernCanvas.Width));
        var canvasHeight = Math.Max(1, (int)Math.Round(_modernCanvas.Height));
        var profile = GetActiveFloorProfile();
        var cropRegion = ModernPngPixelRegionOf(profile.GetEffectiveRecognitionRegion(), canvasWidth, canvasHeight);
        var sourceWidth = Math.Max(1, cropRegion.Width);
        var sourceHeight = Math.Max(1, cropRegion.Height);
        var aspectRatio = sourceWidth / (double)sourceHeight;
        var panel = new StackPanel { Spacing = 12, Width = 430 };
        panel.Children.Add(new TextBlock
        {
            Text = $"当前楼层：{GetModernFloorDisplayName(_activeFloorKey)} · 裁剪区域 {sourceWidth} × {sourceHeight}",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "导出区域为当前裁剪范围；PNG 只包含图层管理器中当前可见的元素；编辑网格、选中框、控制点和未完成的绘制不会导出。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });

        var preset = new ComboBox
        {
            Header = "分辨率预设",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "原始大小（1×）", "高清（2×）", "超清（4×）", "自定义" },
            SelectedIndex = 0
        };
        panel.Children.Add(preset);

        var dimensions = new Grid { ColumnSpacing = 10 };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var widthBox = CreateModernPngDimensionBox("宽度（px）", sourceWidth);
        var heightBox = CreateModernPngDimensionBox("高度（px）", sourceHeight);
        dimensions.Children.Add(widthBox);
        Grid.SetColumn(heightBox, 1);
        dimensions.Children.Add(heightBox);
        panel.Children.Add(dimensions);

        var lockAspect = new ToggleSwitch
        {
            Header = "锁定原始宽高比",
            IsOn = true
        };
        panel.Children.Add(lockAspect);

        var compressionBox = new NumberBox
        {
            Header = "PNG 压缩级别（0–9）",
            Value = 6,
            Minimum = 0,
            Maximum = 9,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        panel.Children.Add(compressionBox);
        panel.Children.Add(new TextBlock
        {
            Text = "PNG 始终是无损格式。较高的压缩级别会减小文件体积，但导出耗时更长；推荐使用 6。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });

        var background = new ComboBox
        {
            Header = "画布背景",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "透明", "白色", "深色" },
            SelectedIndex = 0
        };
        panel.Children.Add(background);

        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(RecognitionRegionRed)
        };
        panel.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出当前楼层为 PNG",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "选择位置并导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var updatingDimensions = false;

        void SetDimensions(double width, double height, bool custom)
        {
            updatingDimensions = true;
            widthBox.Value = Math.Round(width);
            heightBox.Value = Math.Round(height);
            if (custom)
                preset.SelectedIndex = 3;
            updatingDimensions = false;
            UpdateValidation();
        }

        void UpdateValidation()
        {
            var width = widthBox.Value;
            var height = heightBox.Value;
            var compression = compressionBox.Value;
            string? error = null;
            if (!double.IsFinite(width) || !double.IsFinite(height)
                || width < ModernPngMinDimension || height < ModernPngMinDimension
                || width > ModernPngMaxDimension || height > ModernPngMaxDimension)
            {
                error = $"宽度和高度必须在 {ModernPngMinDimension}–{ModernPngMaxDimension} 像素之间。";
            }
            else if ((long)Math.Round(width) * (long)Math.Round(height) > ModernPngMaxPixelCount)
            {
                error = "导出图片不能超过 6710 万像素，请降低宽度或高度。";
            }
            else if (!double.IsFinite(compression) || compression < 0 || compression > 9
                     || Math.Abs(compression - Math.Round(compression)) > .001d)
            {
                error = "PNG 压缩级别必须是 0–9 之间的整数。";
            }
            validationText.Text = error is null
                ? $"预计像素数：{(long)Math.Round(width) * (long)Math.Round(height):N0}"
                : error;
            validationText.Foreground = new SolidColorBrush(error is null ? EditorMuted : RecognitionRegionRed);
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        preset.SelectionChanged += (_, _) =>
        {
            if (updatingDimensions || preset.SelectedIndex == 3)
                return;
            var scale = preset.SelectedIndex switch
            {
                1 => 2d,
                2 => 4d,
                _ => 1d
            };
            SetDimensions(sourceWidth * scale, sourceHeight * scale, custom: false);
        };
        widthBox.ValueChanged += (_, _) =>
        {
            if (updatingDimensions)
                return;
            var width = widthBox.Value;
            if (lockAspect.IsOn && double.IsFinite(width))
                SetDimensions(width, width / aspectRatio, custom: true);
            else
            {
                preset.SelectedIndex = 3;
                UpdateValidation();
            }
        };
        heightBox.ValueChanged += (_, _) =>
        {
            if (updatingDimensions)
                return;
            var height = heightBox.Value;
            if (lockAspect.IsOn && double.IsFinite(height))
                SetDimensions(height * aspectRatio, height, custom: true);
            else
            {
                preset.SelectedIndex = 3;
                UpdateValidation();
            }
        };
        compressionBox.ValueChanged += (_, _) => UpdateValidation();
        UpdateValidation();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var options = new ModernPngExportOptions(
            (int)Math.Round(widthBox.Value),
            (int)Math.Round(heightBox.Value),
            (int)Math.Round(compressionBox.Value),
            background.SelectedIndex switch
            {
                1 => Color.FromArgb(255, 255, 255, 255),
                2 => EditorBackground,
                _ => Color.FromArgb(0, 0, 0, 0)
            });
        var destination = await PickModernPngDestinationAsync();
        if (destination is null)
            return;

        _modernExportInProgress = true;
        if (_modernExportButton is not null)
            _modernExportButton.IsEnabled = false;
        SetModernStatus($"正在导出 {options.Width} × {options.Height} PNG…", false);
        try
        {
            await ExportModernPngAsync(destination, options);
            SetModernStatus("PNG 导出完成。", false);
            await ShowMessageAsync("PNG 导出完成", $"已保存到：\n{destination}");
        }
        catch (Exception exception)
        {
            SetModernStatus("PNG 导出失败。", true);
            await ShowMessageAsync("PNG 导出失败", exception.Message);
        }
        finally
        {
            _modernExportInProgress = false;
            if (_modernExportButton is not null)
                _modernExportButton.IsEnabled = true;
        }
    }

    private static NumberBox CreateModernPngDimensionBox(string header, int value) => new()
    {
        Header = header,
        Value = value,
        Minimum = ModernPngMinDimension,
        Maximum = ModernPngMaxDimension,
        SmallChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };

    private async Task<string?> PickModernPngDestinationAsync()
    {
        try
        {
            var mapName = string.IsNullOrWhiteSpace(_draft?.Title) ? "Map" : _draft.Title;
            var floorName = GetModernFloorDisplayName(_activeFloorKey);
            var picker = new FileSavePicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"IDVB-{SanitizeFileName(mapName)}-{SanitizeFileName(floorName)}",
                DefaultFileExtension = ".png",
                CommitButtonText = "导出",
                FileTypeChoices =
                {
                    { "PNG 图片", new List<string> { ".png" } }
                }
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
                return null;
            return Path.GetExtension(result.Path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? result.Path
                : result.Path + ".png";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开保存选择器", exception.Message);
            return null;
        }
    }

    private async Task ExportModernPngAsync(string destination, ModernPngExportOptions options)
    {
        if (_draft is null)
            throw new InvalidOperationException("当前没有可编辑的地图。");
        var profile = GetActiveFloorProfile();
        var imagePath = GetActiveFloorImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new InvalidOperationException("当前楼层没有可导出的地图图片。");
        var isVisible = IsModernItemVisible;

        // 导出与交互场景完全解耦：直接程序化绘制，不受 ScrollViewer 缩放/裁剪或纹理上限影响。
        var encoded = await Task.Run(() =>
            RenderModernPngExport(profile, imagePath, options, isVisible, _draft.ClassProperties.RemoveBackground));
        await File.WriteAllBytesAsync(destination, encoded);
    }

    private static byte[] EncodeModernPng(byte[] bgraPixels, int width, int height, int compressionLevel)
    {
        using var image = Mat.FromPixelData(
            height,
            width,
            MatType.CV_8UC4,
            bgraPixels,
            checked(width * 4L));
        return image.ImEncode(
            ".png",
            [new ImageEncodingParam(ImwriteFlags.PngCompression, compressionLevel)]);
    }
}
