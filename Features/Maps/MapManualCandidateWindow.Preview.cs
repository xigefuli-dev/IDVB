using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenCvSharp;
using Windows.UI;

namespace IDVBuff.Features.Maps;

public sealed partial class MapManualCandidateWindow
{
    private async Task<FrameworkElement> CreateLivePreviewPanelAsync()
    {
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
        var original = CreatePreviewFrame(
            await MapManualRecognitionWindow.CreateBitmapAsync(recognitionImage),
            "识别区域");
        var detail = CreatePreviewFrame(
            await MapManualRecognitionWindow.CreateBitmapAsync(zoomed),
            "侧门实时放大 · 120%");
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
        var map = choice.Recognition.Map;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var previewPath = _repository.GetFloorOverlayPath(map, primaryFloorKey);
        if (File.Exists(previewPath))
        {
            var sideCenter = MapCandidatePresentationRules
                .ResolveMapSideEntranceCenter(map);
            if (sideCenter is { } center)
            {
                using var source = Cv2.ImRead(previewPath, ImreadModes.Unchanged);
                if (!source.Empty())
                {
                    using var positioned = CreatePositionedPreview(
                        source,
                        center,
                        MapCandidatePresentationRules.MapPreviewZoom,
                        targetX: 0.5d,
                        targetY: MapCandidatePresentationRules.MapSideEntranceTargetY);
                    image.Source = await MapManualRecognitionWindow
                        .CreateBitmapAsync(positioned);
                }
            }
            image.Source ??= new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(previewPath)
            };
        }
        Grid.SetRow(image, 0);
        grid.Children.Add(image);

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
        overlay.Child = details;
        Grid.SetRow(overlay, 1);
        grid.Children.Add(overlay);

        return grid;
    }

    private Mat CreateRecognitionRegionImage()
    {
        var sourceBounds = _frame.ViewportBounds.IsValid
            ? _frame.ViewportBounds
            : _frame.ClientBounds;
        var requested = _recognitionBounds.IsValid
            ? _recognitionBounds
            : sourceBounds;
        if (!sourceBounds.IsValid
            || requested.X <= sourceBounds.X
                && requested.Y <= sourceBounds.Y
                && requested.X + requested.Width >= sourceBounds.X + sourceBounds.Width
                && requested.Y + requested.Height >= sourceBounds.Y + sourceBounds.Height)
        {
            return _frame.Image.Clone();
        }

        var left = (int)Math.Floor(
            (requested.X - sourceBounds.X) / sourceBounds.Width
            * _frame.Image.Width);
        var top = (int)Math.Floor(
            (requested.Y - sourceBounds.Y) / sourceBounds.Height
            * _frame.Image.Height);
        var right = (int)Math.Ceiling(
            (requested.X + requested.Width - sourceBounds.X)
            / sourceBounds.Width * _frame.Image.Width);
        var bottom = (int)Math.Ceiling(
            (requested.Y + requested.Height - sourceBounds.Y)
            / sourceBounds.Height * _frame.Image.Height);
        left = Math.Clamp(left, 0, Math.Max(0, _frame.Image.Width - 1));
        top = Math.Clamp(top, 0, Math.Max(0, _frame.Image.Height - 1));
        right = Math.Clamp(right, left + 1, _frame.Image.Width);
        bottom = Math.Clamp(bottom, top + 1, _frame.Image.Height);
        using var region = new Mat(
            _frame.Image,
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

}
