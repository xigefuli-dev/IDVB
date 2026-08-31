using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void RenderModernEditor()
    {
        if (_modernCanvas is null || _modernScene is null || _draft is null
            || _modernCanvas.Width <= 0 || _modernCanvas.Height <= 0)
            return;
        _modernCanvas.Children.Clear();
        if (!_modernExportRendering && _modernGridVisible)
            AddModernGrid();

        var profile = GetActiveFloorProfile();
        if (IsModernItemVisible("graphics", string.Empty))
        {
            foreach (var annotation in profile.Annotations)
            {
                if (!annotation.IsValid || !IsModernItemVisible("graphics", ModernAnnotationKey(annotation.Id)))
                    continue;
                AddModernAnnotation(annotation);
            }
        }

        if (profile.RecognitionRegion?.IsValid is true
            && IsModernItemVisible("special", "crop"))
        {
            var crop = _modernSelection?.Kind == EditorSelectionKind.Crop
                && _modernPendingBounds?.IsValid is true
                    ? _modernPendingBounds
                    : profile.RecognitionRegion;
            AddModernRectangle(crop, RecognitionRegionRed, dashed: true, thickness: 2.5);
        }

        if (IsModernItemVisible("special", string.Empty))
        {
            foreach (var anchor in profile.Anchors)
            {
                if (anchor.Bounds?.IsValid is not true || !IsModernItemVisible("special", ModernAnchorKey(anchor.Id)))
                    continue;
                AddModernRectangle(ToModernSourceBounds(anchor.Bounds), GetAnchorColor(anchor), dashed: false, thickness: 3.5);
            }
            foreach (var layer in profile.BackgroundLayers)
            {
                if (layer.IsValid && IsModernItemVisible("special", ModernBackgroundKey(layer.Id)))
                    AddModernConcealLayer(layer);
            }
        }

        if (!_modernExportRendering && _modernToolState.PendingMainGate?.IsValid is true)
        {
            AddModernRectangle(
                MapRecognitionCoordinates.ToSourceRectangle(
                    _modernToolState.PendingMainGate,
                    profile.GetEffectiveRecognitionRegion()),
                MainEntranceBlue,
                dashed: true,
                thickness: 3.5);
        }

        if (!_modernExportRendering && _modernInteraction == EditorInteractionKind.Create)
        {
            if (_modernToolState.ActiveTool == MapEditorTool.Line
                && _modernPendingStart?.IsValid is true && _modernPendingEnd?.IsValid is true)
            {
                AddModernLine(_modernPendingStart, _modernPendingEnd, ParseEditorColor(_currentAnnotationColor), true);
            }
            else if (_modernPendingBounds?.IsValid is true)
            {
                var color = _modernToolState.ActiveTool switch
                {
                    MapEditorTool.Gate when !_modernToolState.UsesPrimaryGatePair => SecondFloorPurple,
                    MapEditorTool.Gate => _modernToolState.PendingMainGate is null
                        ? MainEntranceBlue
                        : SideEntranceGreen,
                    MapEditorTool.Crop => RecognitionRegionRed,
                    MapEditorTool.Anchor => OptionalAnchorOrange,
                    _ => ParseEditorColor(_currentAnnotationColor)
                };
                AddModernRectangle(
                    _modernPendingBounds,
                    color,
                    dashed: _modernToolState.ActiveTool is MapEditorTool.Text or MapEditorTool.Crop,
                    thickness: 3);
            }
        }

        if (!_modernExportRendering && _modernToolState.ActiveTool == MapEditorTool.Conceal)
            AddModernConcealPreview();

        if (!_modernExportRendering)
            AddModernSelectionAdorner();
    }

    private void AddModernGrid()
    {
        if (_modernCanvas is null)
            return;
        var spacing = GetModernGridSpacingPixels();
        var brush = new SolidColorBrush(Color.FromArgb(32, 116, 151, 187));
        var majorBrush = new SolidColorBrush(Color.FromArgb(52, 116, 151, 187));
        var thickness = 1d / ModernZoomFactor;
        var lineNumber = 0;
        for (var x = spacing; x < _modernCanvas.Width; x += spacing, lineNumber++)
        {
            _modernCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = _modernCanvas.Height,
                Stroke = lineNumber % 4 == 3 ? majorBrush : brush,
                StrokeThickness = thickness,
                IsHitTestVisible = false
            });
        }
        lineNumber = 0;
        for (var y = spacing; y < _modernCanvas.Height; y += spacing, lineNumber++)
        {
            _modernCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = _modernCanvas.Width,
                Y1 = y,
                Y2 = y,
                Stroke = lineNumber % 4 == 3 ? majorBrush : brush,
                StrokeThickness = thickness,
                IsHitTestVisible = false
            });
        }
    }

    private void AddModernAnnotation(MapAnnotation annotation)
    {
        var color = ParseEditorColor(annotation.EffectiveColorHex);
        if (annotation.Type == MapAnnotationType.Line)
        {
            if (annotation.Start is not null && annotation.End is not null)
                AddModernLine(annotation.Start, annotation.End, color, false);
            return;
        }
        if (annotation.Bounds?.IsValid is not true)
            return;
        if (annotation.Type == MapAnnotationType.Outline)
        {
            AddModernRectangle(annotation.Bounds, color, false, 2.5);
            return;
        }
        AddModernRectangle(annotation.Bounds, color, true, 1.5);
        if (string.IsNullOrWhiteSpace(annotation.Text) || _modernCanvas is null)
            return;
        var bounds = ToModernPixelRect(annotation.Bounds);
        var legacyStyle = annotation.FontFamily is null && annotation.FontSize is null
            && annotation.IsBold is null && annotation.IsItalic is null && annotation.IsStrikethrough is null;
        var label = new TextBlock
        {
            Text = annotation.Text,
            Width = bounds.Width,
            Height = bounds.Height,
            FontSize = CalculateModernAnnotationFontSize(annotation, bounds.Width, bounds.Height),
            Foreground = new SolidColorBrush(color),
            FontWeight = legacyStyle || annotation.IsBold is true
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = annotation.IsItalic is true
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (!string.IsNullOrWhiteSpace(annotation.FontFamily))
            label.FontFamily = new FontFamily(annotation.FontFamily);
        Canvas.SetLeft(label, bounds.X);
        Canvas.SetTop(label, bounds.Y);
        _modernCanvas.Children.Add(label);
        if (annotation.IsStrikethrough is true)
        {
            _modernCanvas.Children.Add(new Line
            {
                X1 = bounds.Left,
                X2 = bounds.Right,
                Y1 = bounds.Top + bounds.Height / 2d,
                Y2 = bounds.Top + bounds.Height / 2d,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = Math.Max(1d, label.FontSize / 14d),
                IsHitTestVisible = false
            });
        }
    }

    private static double CalculateModernAnnotationFontSize(MapAnnotation annotation, double pixelWidth, double pixelHeight)
    {
        var requested = annotation.FontSize ?? CalculateFittingFontSize(annotation.Text ?? string.Empty, pixelWidth, pixelHeight);
        var fitting = CalculateFittingFontSize(annotation.Text ?? string.Empty, pixelWidth, pixelHeight);
        return Math.Min(requested, fitting);
    }

    private void AddModernRectangle(NormalizedRectangle? bounds, Color color, bool dashed, double thickness)
    {
        if (_modernCanvas is null || bounds?.IsValid is not true)
            return;
        var pixels = ToModernPixelRect(bounds);
        var rectangle = new Rectangle
        {
            Width = pixels.Width,
            Height = pixels.Height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness / ModernZoomFactor,
            Fill = new SolidColorBrush(Color.FromArgb(1, color.R, color.G, color.B)),
            IsHitTestVisible = false
        };
        if (dashed)
            rectangle.StrokeDashArray = new DoubleCollection { 6, 4 };
        Canvas.SetLeft(rectangle, pixels.X);
        Canvas.SetTop(rectangle, pixels.Y);
        _modernCanvas.Children.Add(rectangle);
    }

    private void AddModernLine(NormalizedPoint start, NormalizedPoint end, Color color, bool dashed)
    {
        if (_modernCanvas is null)
            return;
        var line = new Line
        {
            X1 = start.X * _modernCanvas.Width,
            Y1 = start.Y * _modernCanvas.Height,
            X2 = end.X * _modernCanvas.Width,
            Y2 = end.Y * _modernCanvas.Height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 3 / ModernZoomFactor,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        if (dashed)
            line.StrokeDashArray = new DoubleCollection { 6, 4 };
        _modernCanvas.Children.Add(line);
    }

    private void AddModernSelectionAdorner()
    {
        if (_modernSelection is null || _modernCanvas is null)
            return;
        if (_modernSelection.Kind == EditorSelectionKind.Annotation
            && FindModernSelectedAnnotation() is { Type: MapAnnotationType.Line } line
            && line.Start is not null && line.End is not null)
        {
            AddModernHandle("start", line.Start.X * _modernCanvas.Width, line.Start.Y * _modernCanvas.Height);
            AddModernHandle("end", line.End.X * _modernCanvas.Width, line.End.Y * _modernCanvas.Height);
            return;
        }
        var bounds = GetModernSelectionSourceBounds();
        if (bounds?.IsValid is not true)
            return;
        AddModernRectangle(bounds, Color.FromArgb(255, 255, 255, 255), true, 1.5);
        var pixels = ToModernPixelRect(bounds);
        foreach (var (handle, x, y) in new[]
        {
            ("nw", pixels.Left, pixels.Top),
            ("n", pixels.Left + pixels.Width / 2, pixels.Top),
            ("ne", pixels.Right, pixels.Top),
            ("e", pixels.Right, pixels.Top + pixels.Height / 2),
            ("se", pixels.Right, pixels.Bottom),
            ("s", pixels.Left + pixels.Width / 2, pixels.Bottom),
            ("sw", pixels.Left, pixels.Bottom),
            ("w", pixels.Left, pixels.Top + pixels.Height / 2)
        })
        {
            AddModernHandle(handle, x, y);
        }
    }

    private void AddModernHandle(string handle, double x, double y)
    {
        if (_modernCanvas is null)
            return;
        var size = 10d / ModernZoomFactor;
        var marker = new Rectangle
        {
            Tag = handle,
            Width = size,
            Height = size,
            RadiusX = 2 / ModernZoomFactor,
            RadiusY = 2 / ModernZoomFactor,
            Fill = new SolidColorBrush(Color.FromArgb(255, 236, 244, 255)),
            Stroke = new SolidColorBrush(AccentBlue),
            StrokeThickness = 1.5 / ModernZoomFactor,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(marker, x - size / 2);
        Canvas.SetTop(marker, y - size / 2);
        _modernCanvas.Children.Add(marker);
    }

    private Rect ToModernPixelRect(NormalizedRectangle bounds) => new(
        bounds.X * (_modernCanvas?.Width ?? 0),
        bounds.Y * (_modernCanvas?.Height ?? 0),
        bounds.Width * (_modernCanvas?.Width ?? 0),
        bounds.Height * (_modernCanvas?.Height ?? 0));

    private NormalizedRectangle ToModernSourceBounds(NormalizedRectangle recognitionRelative) =>
        MapRecognitionCoordinates.ToSourceRectangle(
            recognitionRelative,
            GetActiveFloorProfile().GetEffectiveRecognitionRegion());

    private NormalizedRectangle? ToModernRecognitionBounds(NormalizedRectangle sourceBounds)
    {
        var region = GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        const double epsilon = .000001;
        if (sourceBounds.X < region.X - epsilon || sourceBounds.Y < region.Y - epsilon
            || sourceBounds.X + sourceBounds.Width > region.X + region.Width + epsilon
            || sourceBounds.Y + sourceBounds.Height > region.Y + region.Height + epsilon)
            return null;
        return new NormalizedRectangle
        {
            X = Math.Clamp((sourceBounds.X - region.X) / region.Width, 0, 1),
            Y = Math.Clamp((sourceBounds.Y - region.Y) / region.Height, 0, 1),
            Width = Math.Clamp(sourceBounds.Width / region.Width, 0, 1),
            Height = Math.Clamp(sourceBounds.Height / region.Height, 0, 1)
        };
    }

    private NormalizedRectangle? GetModernSelectionSourceBounds()
    {
        if (_modernSelection is null)
            return null;
        if (_modernSelection.Kind == EditorSelectionKind.Crop)
            return _modernPendingBounds?.IsValid is true
                ? _modernPendingBounds
                : GetActiveFloorProfile().RecognitionRegion;
        if (_modernSelection.Kind == EditorSelectionKind.Anchor)
        {
            var anchor = FindModernSelectedAnchor();
            return anchor?.Bounds?.IsValid is true ? ToModernSourceBounds(anchor.Bounds) : null;
        }
        return FindModernSelectedAnnotation()?.Bounds;
    }

    private RecognitionAnchor? FindModernSelectedAnchor() =>
        _modernSelection is { Kind: EditorSelectionKind.Anchor, Id: { } id }
            ? GetActiveFloorProfile().FindAnchor(id)
            : null;

    private MapAnnotation? FindModernSelectedAnnotation() =>
        _modernSelection is { Kind: EditorSelectionKind.Annotation, Id: { } id }
            ? GetActiveFloorProfile().Annotations.FirstOrDefault(annotation => annotation.Id == id)
            : null;

    private double ModernZoomFactor => _modernExportRendering
        ? 1d
        : Math.Max(.1, _modernViewport?.ZoomFactor ?? 1);

    private double GetModernGridSpacingPixels()
    {
        var spacing = 4d;
        while (spacing * ModernZoomFactor < 24)
            spacing *= 2;
        while (spacing * ModernZoomFactor > 64 && spacing > 4)
            spacing /= 2;
        return spacing;
    }
}
