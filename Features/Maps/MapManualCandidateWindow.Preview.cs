using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenCvSharp;
using Windows.UI;

namespace IDVBuff.Features.Maps;

public sealed partial class MapManualCandidateWindow
{
    private const int CandidatePreviewDecodeWidth = 640;

    public sealed record CandidateLivePreviewAssets(
        ImageSource Original,
        ImageSource Detail);

    /// <summary>
    /// 为候选窗中不依赖实时截图的地图卡片预先解码预览图。后台扫描完成前
    /// 调用它，避免玩家开图后才逐张读取文件、裁剪并 PNG 编码。
    /// </summary>
    public static async Task<IReadOnlyList<ImageSource?>> PrepareChoicePreviewsAsync(
        IReadOnlyList<MapRecognitionChoice> choices,
        MapRepository repository)
    {
        var previews = new ImageSource?[choices.Count];
        for (var index = 0; index < choices.Count; index++)
            previews[index] = await CreateChoicePreviewAsync(choices[index], repository);
        return previews;
    }

    public static async Task<CandidateLivePreviewAssets> PrepareLivePreviewAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        MapScreenRect recognitionBounds)
    {
        using var recognitionImage = CreateRecognitionRegionImage(
            frame,
            recognitionBounds);
        var sideBounds = MapCandidatePresentationRules
            .ResolveLiveSideEntranceBounds(choices);
        var sideCenter = sideBounds is { } bounds
            ? new MapNormalizedPoint(
                Math.Clamp((bounds.CenterX - recognitionBounds.X) / recognitionBounds.Width, 0d, 1d),
                Math.Clamp((bounds.CenterY - recognitionBounds.Y) / recognitionBounds.Height, 0d, 1d))
            : new MapNormalizedPoint(0.5d, 0.5d);
        using var zoomed = CreatePositionedPreview(
            recognitionImage,
            sideCenter,
            MapCandidatePresentationRules.LivePreviewZoom,
            targetX: 0.5d,
            targetY: 0.5d);
        return new CandidateLivePreviewAssets(
            await MapManualRecognitionWindow.CreateBitmapAsync(recognitionImage),
            await MapManualRecognitionWindow.CreateBitmapAsync(zoomed));
    }

    private async Task<FrameworkElement> CreateLivePreviewPanelAsync()
    {
        if (_preloadedLivePreview is { } cached)
            return CreateLivePreviewPanel(cached.Original, cached.Detail);

        using var recognitionImage = CreateRecognitionRegionImage();
        var sideBounds = MapCandidatePresentationRules
            .ResolveLiveSideEntranceBounds(_choices);
        var sideCenter = sideBounds is { } bounds
            ? new MapNormalizedPoint(
                Math.Clamp(
                    (bounds.CenterX - _recognitionBounds.X)
                    / _recognitionBounds.Width,
                    0d,
                    1d),
                Math.Clamp(
                    (bounds.CenterY - _recognitionBounds.Y)
                    / _recognitionBounds.Height,
                    0d,
                    1d))
            : new MapNormalizedPoint(0.5d, 0.5d);
        using var zoomed = CreatePositionedPreview(
            recognitionImage,
            sideCenter,
            MapCandidatePresentationRules.LivePreviewZoom,
            targetX: 0.5d,
            targetY: 0.5d);

        return CreateLivePreviewPanel(
            await MapManualRecognitionWindow.CreateBitmapAsync(recognitionImage),
            await MapManualRecognitionWindow.CreateBitmapAsync(zoomed));
    }

    private static FrameworkElement CreateLivePreviewPanel(
        ImageSource originalSource,
        ImageSource detailSource)
    {
        var panel = new Grid
        {
            RowSpacing = 12,
            Margin = new Thickness(0, 10, 4, 0)
        };
        panel.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        panel.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        var original = CreatePreviewFrame(originalSource, "识别区域");
        var detail = CreatePreviewFrame(detailSource, "扫描门特征实时放大 · 120%");
        Grid.SetRow(original, 0);
        Grid.SetRow(detail, 1);
        panel.Children.Add(original);
        panel.Children.Add(detail);
        return panel;
    }

    private static Border CreatePreviewFrame(ImageSource source, string label)
    {
        var grid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 3, 6, 10))
        };
        grid.Children.Add(new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        });
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 8, 12, 18)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 228, 234))
            }
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = grid
        };
    }

    private async Task<FrameworkElement> CreateChoiceCellAsync(
        MapRecognitionChoice choice,
        int index)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        image.Source = index < _preloadedChoicePreviews?.Count
            ? _preloadedChoicePreviews[index]
            : null;
        image.Source ??= await CreateChoicePreviewAsync(choice, _repository);
        Grid.SetRow(image, 0);
        grid.Children.Add(image);

        var floorKey = choice.Recognition.Result.Floor;
        if (!MapScanFloorRules.IsPrimaryFloor(choice.Recognition.Map, floorKey))
        {
            var floorName = MapCandidatePresentationRules.ResolveFloorDisplayName(
                choice.Recognition.Map,
                floorKey);
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 63, 38, 105)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(5),
                Child = new TextBlock
                {
                    Text = $"{floorName} · 次要门局部预览",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 230, 255))
                }
            };
            Grid.SetRow(badge, 0);
            grid.Children.Add(badge);
        }

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 8, 12, 18)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(10, 5, 10, 6)
        };
        var details = new StackPanel { Spacing = 2 };
        details.Children.Add(new TextBlock
        {
            Text = $"{index + 1}. {choice.Recognition.Map.DisplayName}",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        });
        details.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(choice.EvidenceLabel)
                ? $"几何误差 {choice.VectorError:F3} · 置信度 {choice.RawConfidence:P0}"
                : choice.EvidenceLabel,
            FontSize = 13,
            Foreground = new SolidColorBrush(choice.IsReferenceOnly
                ? Color.FromArgb(255, 244, 190, 90)
                : Color.FromArgb(255, 150, 225, 170))
        });
        if (choice.TraditionalScore is { } traditional)
        {
            details.Children.Add(CreateEvidenceText(
                $"传统算法 {ToConfidenceText(traditional)} · {traditional:P0}"));
        }
        if (choice.ModelProbability is { } model)
        {
            details.Children.Add(CreateEvidenceText(
                $"空间匹配 {ToConfidenceText(model)} · {model:P0}"
                + (string.IsNullOrWhiteSpace(choice.ModelVersion)
                    ? string.Empty
                    : $" · {choice.ModelVersion}")
                + (choice.ModelMatchedCenterX is { } x
                    && choice.ModelMatchedCenterY is { } y
                    ? $" · {choice.ModelMatchedFloorKey.ToUpperInvariant()}"
                        + $" ({x:P0}, {y:P0})"
                    : string.Empty)));
        }
        else if (!string.IsNullOrWhiteSpace(choice.ModelFailureReason))
        {
            details.Children.Add(CreateEvidenceText(
                $"空间匹配失败 · {choice.ModelFailureReason}"));
        }
        if (choice.FusionScore is { } fusion)
            details.Children.Add(CreateEvidenceText($"融合排序 · {fusion:P0}"));
        overlay.Child = details;
        Grid.SetRow(overlay, 1);
        grid.Children.Add(overlay);

        return grid;
    }

    private static async Task<ImageSource?> CreateChoicePreviewAsync(
        MapRecognitionChoice choice,
        MapRepository repository)
    {
        var map = choice.Recognition.Map;
        var previewPath = repository.GetFloorOverlayPath(
            map,
            choice.Recognition.Result.Floor);
        if (!File.Exists(previewPath))
            return null;

        var previewPlan = MapCandidatePresentationRules.ResolveMapPreviewPlan(
            map,
            choice.Recognition.Result.Floor);
        if (previewPlan is { } plan)
        {
            using var source = Cv2.ImRead(previewPath, ImreadModes.Unchanged);
            if (!source.Empty())
            {
                using var positioned = CreatePositionedPreview(
                    source,
                    plan.Center,
                    plan.Zoom,
                    plan.TargetX,
                    plan.TargetY);
                using var preview = ResizeForPreview(positioned);
                return await MapManualRecognitionWindow.CreateBitmapAsync(preview);
            }
        }

        return new BitmapImage
        {
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            DecodePixelWidth = CandidatePreviewDecodeWidth,
            UriSource = new Uri(previewPath)
        };
    }

    private static Mat ResizeForPreview(Mat source)
    {
        if (source.Width <= CandidatePreviewDecodeWidth)
            return source.Clone();
        var resized = new Mat();
        Cv2.Resize(
            source,
            resized,
            new OpenCvSharp.Size(
                CandidatePreviewDecodeWidth,
                Math.Max(1, source.Height * CandidatePreviewDecodeWidth / source.Width)),
            0,
            0,
            InterpolationFlags.Area);
        return resized;
    }

    private Mat CreateRecognitionRegionImage() => CreateRecognitionRegionImage(
        _frame,
        _recognitionBounds);

    private static Mat CreateRecognitionRegionImage(
        CapturedGameFrame frame,
        MapScreenRect recognitionBounds)
    {
        var sourceBounds = frame.ViewportBounds.IsValid
            ? frame.ViewportBounds
            : frame.ClientBounds;
        var requested = recognitionBounds.IsValid
            ? recognitionBounds
            : sourceBounds;
        if (!sourceBounds.IsValid
            || requested.X <= sourceBounds.X
                && requested.Y <= sourceBounds.Y
                && requested.X + requested.Width >= sourceBounds.X + sourceBounds.Width
                && requested.Y + requested.Height >= sourceBounds.Y + sourceBounds.Height)
        {
            return frame.Image.Clone();
        }

        var left = (int)Math.Floor(
            (requested.X - sourceBounds.X) / sourceBounds.Width
            * frame.Image.Width);
        var top = (int)Math.Floor(
            (requested.Y - sourceBounds.Y) / sourceBounds.Height
            * frame.Image.Height);
        var right = (int)Math.Ceiling(
            (requested.X + requested.Width - sourceBounds.X)
            / sourceBounds.Width * frame.Image.Width);
        var bottom = (int)Math.Ceiling(
            (requested.Y + requested.Height - sourceBounds.Y)
            / sourceBounds.Height * frame.Image.Height);
        left = Math.Clamp(left, 0, Math.Max(0, frame.Image.Width - 1));
        top = Math.Clamp(top, 0, Math.Max(0, frame.Image.Height - 1));
        right = Math.Clamp(right, left + 1, frame.Image.Width);
        bottom = Math.Clamp(bottom, top + 1, frame.Image.Height);
        using var region = new Mat(
            frame.Image,
            new OpenCvSharp.Rect(left, top, right - left, bottom - top));
        return region.Clone();
    }

    private static Mat CreatePositionedPreview(
        Mat source,
        MapNormalizedPoint center,
        double zoom,
        double targetX,
        double targetY)
    {
        var output = new Mat(source.Rows, source.Cols, source.Type(), Scalar.Black);
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, zoom);
        matrix.Set(0, 1, 0d);
        matrix.Set(0, 2, (source.Width * targetX) - (source.Width * center.X * zoom));
        matrix.Set(1, 0, 0d);
        matrix.Set(1, 1, zoom);
        matrix.Set(1, 2, (source.Height * targetY) - (source.Height * center.Y * zoom));
        Cv2.WarpAffine(
            source,
            output,
            matrix,
            new OpenCvSharp.Size(source.Width, source.Height),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);
        return output;
    }

    private static TextBlock CreateEvidenceText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(
            Color.FromArgb(255, 177, 190, 207)),
        TextWrapping = TextWrapping.Wrap
    };

    private static string ToConfidenceText(double score) => score switch
    {
        >= 0.85d => "高",
        >= 0.60d => "中",
        _ => "低"
    };

}
