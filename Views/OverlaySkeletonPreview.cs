using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.UI;
using IDVBuff.Features.Maps;

namespace IDVBuff.Views;

internal enum OverlayPreviewPart
{
    None,
    Status,
    MiniMap
}

internal sealed record OverlaySkeletonPreviewState(
    int ResolutionWidth,
    int ResolutionHeight,
    OverlayPreviewPart ActivePart,
    double StatusOpacity,
    double StatusScale,
    double StatusOffsetX,
    double StatusOffsetY,
    double MiniMapOpacity,
    double MiniMapOffsetX,
    double MiniMapOffsetY,
    double MiniMapPixelWidth,
    double MiniMapPixelHeight,
    bool ShowGateMarkers,
    bool ShowAuxiliaryAnchors,
    bool ShowTextAnnotations,
    bool ShowBoxAnnotations,
    bool ShowLineAnnotations,
    bool ShowFloor);

/// <summary>
/// A deliberately synthetic overlay preview. It mirrors display settings while
/// ensuring that no real map or live match information appears in the shell.
/// </summary>
internal sealed class OverlaySkeletonPreview : Grid
{
    // Keep motion policy centralized. Future spring/key-frame motion can replace
    // these transitions without changing the settings-to-preview data contract.
    private static readonly TimeSpan PartMotionDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan PartOpacityDuration = TimeSpan.FromMilliseconds(100);
    // 15% first enlargement plus the requested additional 10% enlargement.
    private const double ScreenWidth = 385d;
    private readonly TextBlock _aspectLabel;
    private readonly Canvas _screen;
    private readonly Border _statusOutline;
    private readonly Border _statusContent;
    private readonly Border _miniMapOutline;
    private readonly Border _miniMapImage;
    private readonly FrameworkElement _gateMarkers;
    private readonly FrameworkElement _auxiliaryAnchors;
    private readonly FrameworkElement _textAnnotation;
    private readonly FrameworkElement _boxAnnotation;
    private readonly FrameworkElement _lineAnnotation;
    private readonly FrameworkElement _floorLabel;
    private readonly Brush _accentBrush = FluentTheme.Brush("AccentFillColorDefaultBrush");
    private readonly Brush _inactiveOutlineBrush = new SolidColorBrush(Colors.Transparent);

    public OverlaySkeletonPreview()
    {
        Width = 425;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;

        var panel = new Border
        {
            Padding = new Thickness(15, 12, 15, 15),
            CornerRadius = new CornerRadius(12),
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow()
        };
        Children.Add(panel);

        var stack = new StackPanel { Spacing = 9 };
        panel.Child = stack;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "显示预览",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _aspectLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_aspectLabel, 1);
        header.Children.Add(_aspectLabel);
        stack.Children.Add(header);

        _screen = new Canvas
        {
            Width = ScreenWidth,
            Height = 217,
            Background = FluentTheme.Brush("ApplicationPageBackgroundThemeBrush"),
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ScreenWidth, 217) }
        };
        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderBrush = FluentTheme.Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = _screen
        });

        _statusContent = new Border
        {
            Width = 126,
            Height = 52,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(210, 15, 15, 15)),
            Child = new Viewbox
            {
                Stretch = Stretch.Fill,
                Child = new Border
                {
                    Width = 126,
                    Height = 52,
                    Padding = new Thickness(7, 5, 7, 5),
                    Child = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "状态占位",
                                FontSize = 9,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(Color.FromArgb(255, 93, 184, 255))
                            },
                            new TextBlock
                            {
                                Text = "正在显示预览效果",
                                FontSize = 8,
                                Foreground = new SolidColorBrush(Colors.White)
                            },
                            new TextBlock
                            {
                                Text = "占位文字 · 非实时状态",
                                FontSize = 7,
                                Foreground = new SolidColorBrush(Color.FromArgb(210, 210, 210, 210))
                            }
                        }
                    }
                }
            }
        };
        _statusOutline = CreateOutline(_statusContent);
        PrepareAnimatedPart(_statusOutline);
        _statusContent.OpacityTransition = new ScalarTransition
        {
            Duration = PartOpacityDuration
        };
        _screen.Children.Add(_statusOutline);

        var mapSurface = new Grid { Width = 118, Height = 82 };
        _miniMapImage = new Border
        {
            Background = FluentTheme.Brush("ControlFillColorSecondaryBrush"),
            BorderBrush = FluentTheme.Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        mapSurface.Children.Add(_miniMapImage);
        mapSurface.Children.Add(CreateMapPlaceholderLines());
        mapSurface.Children.Add(new TextBlock
        {
            Text = "地图占位",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        _gateMarkers = CreateGateMarkers();
        _auxiliaryAnchors = CreateAuxiliaryAnchors();
        _textAnnotation = CreateTextAnnotation();
        _boxAnnotation = CreateBoxAnnotation();
        _lineAnnotation = CreateLineAnnotation();
        _floorLabel = new TextBlock
        {
            Text = "1F",
            Margin = new Thickness(5, 3, 0, 0),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        mapSurface.Children.Add(_gateMarkers);
        mapSurface.Children.Add(_auxiliaryAnchors);
        mapSurface.Children.Add(_textAnnotation);
        mapSurface.Children.Add(_boxAnnotation);
        mapSurface.Children.Add(_lineAnnotation);
        mapSurface.Children.Add(_floorLabel);

        _miniMapOutline = CreateOutline(new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = mapSurface
        });
        PrepareAnimatedPart(_miniMapOutline);
        _miniMapImage.OpacityTransition = new ScalarTransition
        {
            Duration = PartOpacityDuration
        };
        _screen.Children.Add(_miniMapOutline);
    }

    public void Update(OverlaySkeletonPreviewState state)
    {
        var resolutionWidth = Math.Max(1, state.ResolutionWidth);
        var resolutionHeight = Math.Max(1, state.ResolutionHeight);
        var screenHeight = ScreenWidth * resolutionHeight / resolutionWidth;
        _screen.Height = screenHeight;
        _screen.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, ScreenWidth, screenHeight)
        };
        _aspectLabel.Text = $"{resolutionWidth}×{resolutionHeight}";

        var statusScale = Math.Clamp(state.StatusScale, 0d, 1d);
        var statusWidth = 260d * ScreenWidth / resolutionWidth * statusScale;
        var statusHeight = 78d * screenHeight / resolutionHeight * statusScale;
        var mapWidth = Math.Clamp(
            state.MiniMapPixelWidth * ScreenWidth / resolutionWidth,
            0d,
            ScreenWidth);
        var mapHeight = Math.Clamp(
            state.MiniMapPixelHeight * screenHeight / resolutionHeight,
            0d,
            screenHeight);
        // The actual overlay maps are larger than the measured status panel.
        // Preserve that invariant even for the generic fallback placeholder.
        statusWidth = Math.Clamp(statusWidth, 0d, ScreenWidth);
        statusHeight = Math.Clamp(statusHeight, 0d, screenHeight);
        _statusContent.Width = statusWidth;
        _statusContent.Height = statusHeight;
        _statusContent.Opacity = Math.Clamp(state.StatusOpacity, 0d, 1d);
        const float previewMargin = 3f;
        var layout = OverlayNormalizedLayout.Resolve(
            new System.Drawing.SizeF(
                (float)ScreenWidth - (previewMargin * 2f),
                Math.Max(0f, (float)screenHeight - (previewMargin * 2f))),
            statusWidth > 0d && statusHeight > 0d
                ? new System.Drawing.SizeF((float)statusWidth, (float)statusHeight)
                : null,
            new System.Drawing.PointF(
                (float)state.StatusOffsetX,
                (float)state.StatusOffsetY),
            mapWidth > 0d && mapHeight > 0d
                ? new System.Drawing.SizeF((float)mapWidth, (float)mapHeight)
                : null,
            new System.Drawing.PointF(
                (float)state.MiniMapOffsetX,
                (float)state.MiniMapOffsetY),
            3f);
        var statusBounds = layout.Status ?? System.Drawing.RectangleF.Empty;
        _statusOutline.Translation = new Vector3(
            statusBounds.X + previewMargin,
            statusBounds.Y + previewMargin,
            0f);

        var mapBounds = layout.MiniMap ?? System.Drawing.RectangleF.Empty;
        _miniMapOutline.Width = mapBounds.Width;
        _miniMapOutline.Height = mapBounds.Height;
        _miniMapOutline.Scale = Vector3.One;
        _miniMapImage.Opacity = Math.Clamp(state.MiniMapOpacity, 0d, 1d);
        _miniMapOutline.Translation = new Vector3(
            mapBounds.X + previewMargin,
            mapBounds.Y + previewMargin,
            0f);

        _gateMarkers.Visibility = ToVisibility(state.ShowGateMarkers);
        _auxiliaryAnchors.Visibility = ToVisibility(state.ShowAuxiliaryAnchors);
        _textAnnotation.Visibility = ToVisibility(state.ShowTextAnnotations);
        _boxAnnotation.Visibility = ToVisibility(state.ShowBoxAnnotations);
        _lineAnnotation.Visibility = ToVisibility(state.ShowLineAnnotations);
        _floorLabel.Visibility = ToVisibility(state.ShowFloor);

        _statusOutline.BorderBrush = state.ActivePart == OverlayPreviewPart.Status
            ? _accentBrush
            : _inactiveOutlineBrush;
        _miniMapOutline.BorderBrush = state.ActivePart == OverlayPreviewPart.MiniMap
            ? _accentBrush
            : _inactiveOutlineBrush;
    }

    private Border CreateOutline(UIElement child) => new()
    {
        Padding = new Thickness(2),
        BorderThickness = new Thickness(2),
        BorderBrush = _inactiveOutlineBrush,
        CornerRadius = new CornerRadius(7),
        Child = child
    };

    private static void PrepareAnimatedPart(FrameworkElement element)
    {
        element.CenterPoint = Vector3.Zero;
        element.TranslationTransition = new Vector3Transition
        {
            Duration = PartMotionDuration
        };
        element.ScaleTransition = new Vector3Transition
        {
            Duration = PartMotionDuration
        };
    }

    private static Visibility ToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private static Canvas CreateMapPlaceholderLines()
    {
        var canvas = new Canvas { Opacity = 0.35, IsHitTestVisible = false };
        var brush = FluentTheme.Brush("TextFillColorSecondaryBrush");
        AddLine(canvas, 8, 18, 106, 18, brush, 1);
        AddLine(canvas, 25, 8, 25, 72, brush, 1);
        AddLine(canvas, 8, 62, 108, 35, brush, 1);
        AddLine(canvas, 72, 8, 96, 74, brush, 1);
        return canvas;
    }

    private static Grid CreateGateMarkers()
    {
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = "▰",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 91, 91)),
            FontSize = 13,
            Margin = new Thickness(94, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        grid.Children.Add(new TextBlock
        {
            Text = "▰",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 182, 72)),
            FontSize = 11,
            Margin = new Thickness(6, 54, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        return grid;
    }

    private static Grid CreateAuxiliaryAnchors()
    {
        var grid = new Grid();
        grid.Children.Add(CreateAnchor(42, 10));
        grid.Children.Add(CreateAnchor(78, 57));
        return grid;
    }

    private static Border CreateAnchor(double left, double top) => new()
    {
        Width = 7,
        Height = 7,
        Margin = new Thickness(left, top, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(255, 77, 208, 225)),
        BorderBrush = new SolidColorBrush(Colors.White),
        BorderThickness = new Thickness(1)
    };

    private static TextBlock CreateTextAnnotation() => new()
    {
        Text = "标注",
        FontSize = 7,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 219, 94)),
        Margin = new Thickness(55, 35, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };

    private static Rectangle CreateBoxAnnotation() => new()
    {
        Width = 25,
        Height = 17,
        Stroke = new SolidColorBrush(Color.FromArgb(255, 123, 97, 255)),
        StrokeThickness = 1.5,
        Margin = new Thickness(29, 47, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };

    private static Line CreateLineAnnotation()
    {
        var line = new Line
        {
            X1 = 66,
            Y1 = 24,
            X2 = 101,
            Y2 = 51,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 75, 199, 130)),
            StrokeThickness = 2
        };
        return line;
    }

    private static void AddLine(
        Canvas canvas,
        double x1,
        double y1,
        double x2,
        double y2,
        Brush stroke,
        double thickness) => canvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = stroke,
            StrokeThickness = thickness
        });
}
