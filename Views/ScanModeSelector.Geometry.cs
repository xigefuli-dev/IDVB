using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private CompositionPropertySet? _geometry;
    private readonly List<(Border Host, CompositionRoundedRectangleGeometry Geometry,
        CompositionColorBrush Fill, CompositionColorBrush Stroke)> _pills = [];
    private CompositionLinearGradientBrush? _selectionFill;
    private double _geometryTrackWidth;

    private void EnsureGeometry()
    {
        if (_geometry is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_track).Compositor;
        _geometry = compositor.CreatePropertySet();
        _geometry.InsertScalar("Width", (float)_track.ActualWidth);
        _geometry.InsertScalar("Expansion", (float)_appearanceFrame.Expansion);
        _geometry.InsertScalar("Index", (float)_appearanceFrame.Index);
        _geometry.InsertScalar("Pointer", 0);
        _geometry.InsertScalar("Dragging", 0);
        _geometry.InsertScalar("Segments", _visibleSegments);
        _geometry.InsertScalar("SegmentWidth", 1);
        _geometry.InsertScalar("SelectionX", 4);
        BindGeometry(_geometry, "SegmentWidth", "Max(1, pose.Width / (3 + pose.Expansion))");
        BindGeometry(_geometry, "SelectionX",
            "Clamp(pose.Dragging > 0 ? pose.Pointer / pose.SegmentWidth - 0.5 : pose.Index, 0, pose.Dragging > 0 ? Min(pose.Segments - 1, 2 + pose.Expansion) : 2 + pose.Expansion) * pose.SegmentWidth + 4");

        AddPill(_selection, 0, 0);
        AddPill(_fastGlow, 0, 0);
        AddPill(_glowOuter, 72, -36);
        AddPill(_glowInner, 44, -22);
        AddPill(_qualityHaloOuter, 26, -13);
        AddPill(_qualityHaloInner, 10, -5);
        _selectionFill = compositor.CreateLinearGradientBrush();
        _selectionFill.StartPoint = new Vector2(0, .2f);
        _selectionFill.EndPoint = new Vector2(1, .8f);
        foreach (var offset in new[] { 0f, .5f, 1f })
            _selectionFill.ColorStops.Add(compositor.CreateColorGradientStop(offset, Microsoft.UI.Colors.Transparent));
        var selectionVisual = (ShapeVisual)ElementCompositionPreview.GetElementChildVisual(_selection);
        ((CompositionSpriteShape)selectionVisual.Shapes[0]).FillBrush = _selectionFill;

        // Text is measured once at the rail center. All slots use the same
        // unrounded Composition coordinates as the pill, including the reveal.
        for (var i = 0; i < _labels.Length; i++)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_labels[i]);
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            BindGeometry(visual, "Translation.X", i == 3
                ? "(2.5 + pose.Expansion) * pose.SegmentWidth - pose.Width / 2"
                : $"({i} + 0.5) * pose.SegmentWidth - pose.Width / 2");
        }
        for (var i = 0; i < _dividers.Length; i++)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_dividers[i], true);
            BindGeometry(ElementCompositionPreview.GetElementVisual(_dividers[i]), "Translation.X",
                $"{i + 1} * pose.SegmentWidth");
        }
        BindGeometry(ElementCompositionPreview.GetElementVisual(_speedField), "Translation.X", "pose.SelectionX - 4");
        var speedClip = compositor.CreateInsetClip();
        BindGeometry(speedClip, "RightInset", "Max(0, pose.Width - pose.SegmentWidth)");
        ElementCompositionPreview.GetElementVisual(_speedField).Clip = speedClip;
        _deepTrack.BindFillBoundary(_geometry);
    }

    private void BindGeometry(CompositionObject target, string property, string expression)
    {
        using var animation = _geometry!.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("pose", _geometry);
        target.StartAnimation(property, animation);
    }

    private void AddPill(Border host, int extraWidth, int offset)
    {
        var compositor = _geometry!.Compositor;
        // XAML supplies only the fixed vertical origin. Animated width belongs
        // to a rounded geometry, so neither layout nor corner scaling is needed.
        var height = (int)host.Height;
        host.Width = 1;
        host.Height = 1;
        host.CornerRadius = new CornerRadius(0);
        host.VerticalAlignment = VerticalAlignment.Top;
        host.Background = null;
        host.BorderBrush = null;
        host.BorderThickness = new Thickness(0);
        var visual = compositor.CreateShapeVisual();
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(.5f);
        geometry.CornerRadius = new Vector2((float)(height / 2.0 - .5));
        var fill = compositor.CreateColorBrush();
        var stroke = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = fill;
        shape.StrokeBrush = stroke;
        shape.StrokeThickness = 1;
        visual.Shapes.Add(shape);
        BindGeometry(geometry, "Size", $"Vector2(Max(1, pose.SegmentWidth - 9 + {extraWidth}), {height - 1})");
        BindGeometry(visual, "Size", $"Vector2(Max(1, pose.SegmentWidth - 8 + {extraWidth}), {height})");
        ElementCompositionPreview.SetElementChildVisual(host, visual);
        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        hostVisual.Properties.InsertVector3("Translation", new Vector3(0, (58 - height) / 2f, 0));
        BindGeometry(hostVisual, "Translation.X", $"pose.SelectionX + {offset}");
        BindGeometry(hostVisual, "CenterPoint", $"Vector3((pose.SegmentWidth - 8 + {extraWidth}) / 2, {height / 2}, 0)");
        _pills.Add((host, geometry, fill, stroke));
    }

    private void TransitionGeometry(bool animate)
    {
        EnsureGeometry();
        _geometry!.InsertScalar("Segments", _visibleSegments);
        AnimateGeometryScalar("Expansion", _appearanceStart.Expansion, _appearanceTarget.Expansion, 150, animate);
        AnimateGeometryScalar("Index", _appearanceStart.Index, _appearanceTarget.Index,
            _selectionTransitionMilliseconds, animate);
        UpdateGeometryPointer();
    }

    private void AnimateGeometryScalar(string property, double from, double to, double duration, bool animate)
    {
        _geometry!.StopAnimation(property);
        _geometry.InsertScalar(property, (float)to);
        if (!animate || from == to) return;
        using var animation = _geometry.Compositor.CreateScalarKeyFrameAnimation();
        using var linear = _geometry.Compositor.CreateLinearEasingFunction();
        // Sample the shared response curve; the compositor interpolates these
        // values at the display cadence even when the UI thread is busy.
        for (var i = 0; i <= 30; i++)
        {
            var t = i / 30f;
            animation.InsertKeyFrame(t, (float)Mix(from, to,
                ScanModeVisualRules.ResponseProgress(t * duration, duration)), linear);
        }
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        _geometry.StartAnimation(property, animation);
    }

    private void UpdateGeometryPointer()
    {
        if (_geometry is null) return;
        _geometry.InsertScalar("Pointer", (float)((_dragTrackX ?? 0) - _dragGrabOffset));
        _geometry.InsertScalar("Dragging", _isPointerDragging && _dragPointer.HasValue ? 1 : 0);
    }

    private void RenderGeometrySurface(AppearanceFrame frame)
    {
        EnsureGeometry();
        for (var i = 0; i < 3; i++)
            _selectionFill!.ColorStops[i].Color = _pillFill.GradientStops[i].Color;
        foreach (var pill in _pills)
        {
            var selected = pill.Host == _selection;
            var halo = pill.Host == _qualityHaloInner || pill.Host == _qualityHaloOuter;
            pill.Fill.Color = halo ? _haloFill.Color : frame.Accent;
            pill.Stroke.Color = selected ? _pillStroke.Color : halo ? _haloStroke.Color : Microsoft.UI.Colors.Transparent;
            if (selected) pill.Geometry.CornerRadius = new Vector2((float)Mix(24.5, 11.5, frame.Deep));
        }
    }
}
