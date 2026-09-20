using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed partial class SurveyCanvasView
{
    private const double MinimumTransformScale = 0.01d;
    private readonly Canvas _transformBox = new() { IsHitTestVisible = true };
    private readonly Line[] _transformOutline =
    [
        new() { IsHitTestVisible = false },
        new() { IsHitTestVisible = false },
        new() { IsHitTestVisible = false },
        new() { IsHitTestVisible = false }
    ];
    private readonly Dictionary<TransformHandle, Border> _transformHandles = [];
    private TransformHandle? _activeTransformHandle;
    private SurveyLayerTransform _transformStart;
    private Point _transformStartPointer;
    private double _transformLayerWidth;
    private double _transformLayerHeight;
    private SurveyWorldPoint _transformFixedLocal;
    private SurveyWorldPoint _transformFixedWorld;
    private SurveyWorldPoint _rotationCenterWorld;
    private double _rotationStartAngle;

    private enum TransformHandle
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
        RotateTopLeft,
        RotateTopRight,
        RotateBottomRight,
        RotateBottomLeft
    }

    private void InitializeTransformBox()
    {
        _transformBox.Visibility = Visibility.Collapsed;
        foreach (var edge in _transformOutline)
        {
            edge.Stroke = new SolidColorBrush(Color.FromArgb(255, 91, 176, 255));
            _transformBox.Children.Add(edge);
        }

        foreach (var handle in Enum.GetValues<TransformHandle>())
        {
            var rotation = IsRotationHandle(handle);
            var visual = new Border
            {
                Tag = handle,
                Background = new SolidColorBrush(rotation
                    ? Color.FromArgb(18, 255, 176, 65)
                    : Color.FromArgb(255, 245, 249, 255)),
                BorderBrush = new SolidColorBrush(rotation
                    ? Color.FromArgb(255, 255, 176, 65)
                    : Color.FromArgb(255, 46, 132, 225)),
                BorderThickness = new Thickness(rotation ? 1.5d : 1d),
                CornerRadius = new CornerRadius(rotation ? 99d : 1.5d)
            };
            ToolTipService.SetToolTip(visual, rotation
                ? "拖动旋转"
                : IsCornerHandle(handle)
                    ? "拖动等比缩放；按住 Shift 自由缩放"
                    : handle is TransformHandle.Top or TransformHandle.Bottom
                        ? "沿 Y 方向缩放"
                        : "沿 X 方向缩放");
            visual.PointerPressed += TransformHandle_PointerPressed;
            visual.PointerMoved += TransformHandle_PointerMoved;
            visual.PointerReleased += TransformHandle_PointerReleased;
            visual.PointerCanceled += TransformHandle_PointerCanceled;
            _transformHandles[handle] = visual;
            _transformBox.Children.Add(visual);
        }

        // Grid paints later children above earlier ones, so adding the viewport
        // overlay after the world canvas is sufficient. Canvas.SetZIndex is not
        // valid for this Grid child and causes WinUI to fail fast during layout.
        Children.Add(_transformBox);
    }

    private void UpdateTransformBox()
    {
        if (_disposed
            || ActiveTool != SurveyEditorTool.Select
            || _primaryLayerId is not { } layerId
            || !_visuals.TryGetValue(layerId, out var wrapper)
            || wrapper.Visibility != Visibility.Visible
            || wrapper.Tag is not SurveyMapLayer layer)
        {
            _transformBox.Visibility = Visibility.Collapsed;
            return;
        }

        var transform = _activeTransformHandle is not null ? _dragTransform : layer.EffectiveTransform;
        var width = wrapper.Width;
        var height = wrapper.Height;
        var localCorners = new[]
        {
            new SurveyWorldPoint(0d, 0d),
            new SurveyWorldPoint(width, 0d),
            new SurveyWorldPoint(width, height),
            new SurveyWorldPoint(0d, height)
        };
        var zoom = Math.Max(0.1d, _viewportTransform.ScaleX);
        var corners = localCorners
            .Select(transform.Transform)
            .Select(point => new Point(
                ((point.X + _originX) * zoom) + _viewportTransform.TranslateX,
                ((point.Y + _originY) * zoom) + _viewportTransform.TranslateY))
            .ToArray();
        if (corners.Any(point =>
            !double.IsFinite(point.X)
            || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 10_000_000d
            || Math.Abs(point.Y) > 10_000_000d))
        {
            _transformBox.Visibility = Visibility.Collapsed;
            return;
        }
        for (var index = 0; index < _transformOutline.Length; index++)
        {
            var edge = _transformOutline[index];
            var from = corners[index];
            var to = corners[(index + 1) % corners.Length];
            edge.X1 = from.X;
            edge.Y1 = from.Y;
            edge.X2 = to.X;
            edge.Y2 = to.Y;
        }

        var center = new Point(
            corners.Average(point => point.X),
            corners.Average(point => point.Y));
        var positions = new Dictionary<TransformHandle, Point>
        {
            [TransformHandle.TopLeft] = corners[0],
            [TransformHandle.Top] = Midpoint(corners[0], corners[1]),
            [TransformHandle.TopRight] = corners[1],
            [TransformHandle.Right] = Midpoint(corners[1], corners[2]),
            [TransformHandle.BottomRight] = corners[2],
            [TransformHandle.Bottom] = Midpoint(corners[2], corners[3]),
            [TransformHandle.BottomLeft] = corners[3],
            [TransformHandle.Left] = Midpoint(corners[3], corners[0])
        };
        const double handleSize = 10d;
        const double rotationSize = 15d;
        const double rotationOffset = 24d;
        positions[TransformHandle.RotateTopLeft] = OffsetFromCenter(corners[0], center, rotationOffset);
        positions[TransformHandle.RotateTopRight] = OffsetFromCenter(corners[1], center, rotationOffset);
        positions[TransformHandle.RotateBottomRight] = OffsetFromCenter(corners[2], center, rotationOffset);
        positions[TransformHandle.RotateBottomLeft] = OffsetFromCenter(corners[3], center, rotationOffset);

        foreach (var edge in _transformOutline)
            edge.StrokeThickness = 1.5d;
        foreach (var pair in positions)
        {
            var visual = _transformHandles[pair.Key];
            var size = IsRotationHandle(pair.Key) ? rotationSize : handleSize;
            visual.Width = size;
            visual.Height = size;
            Canvas.SetLeft(visual, pair.Value.X - (size / 2d));
            Canvas.SetTop(visual, pair.Value.Y - (size / 2d));
        }
        _transformBox.Width = Math.Max(0d, ActualWidth);
        _transformBox.Height = Math.Max(0d, ActualHeight);
        _transformBox.Visibility = Visibility.Visible;
    }

    private void TransformHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_temporaryNavigationActive
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginPan(e, temporary: true);
            return;
        }
        if (sender is not Border { Tag: TransformHandle handle } visual
            || _primaryLayerId is not { } layerId
            || !_visuals.TryGetValue(layerId, out var wrapper)
            || wrapper.Tag is not SurveyMapLayer { IsLocked: false } layer
            || wrapper.RenderTransform is not CompositeTransform composite
            || !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
            return;

        _activeTransformHandle = handle;
        _dragLayerId = layerId;
        _dragTransform = layer.EffectiveTransform;
        _transformStart = _dragTransform;
        _dragVisualTransform = composite;
        _transformLayerWidth = wrapper.Width;
        _transformLayerHeight = wrapper.Height;
        _transformStartPointer = e.GetCurrentPoint(_canvas).Position;

        if (IsRotationHandle(handle))
        {
            var localCenter = new SurveyWorldPoint(_transformLayerWidth / 2d, _transformLayerHeight / 2d);
            _rotationCenterWorld = _transformStart.Transform(localCenter);
            _rotationStartAngle = Angle(
                _transformStartPointer.X - _originX - _rotationCenterWorld.X,
                _transformStartPointer.Y - _originY - _rotationCenterWorld.Y);
        }
        else
        {
            _transformFixedLocal = OppositeAnchor(handle, _transformLayerWidth, _transformLayerHeight);
            _transformFixedWorld = _transformStart.Transform(_transformFixedLocal);
        }

        visual.CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void TransformHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeTransformHandle is not { } handle
            || _dragVisualTransform is null)
            return;
        var pointer = e.GetCurrentPoint(_canvas).Position;
        var pointerWorld = new SurveyWorldPoint(pointer.X - _originX, pointer.Y - _originY);
        _dragTransform = IsRotationHandle(handle)
            ? CalculateRotation(pointerWorld)
            : CalculateScale(handle, pointerWorld, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
        ApplyLiveTransform(_dragTransform);
        UpdateTransformBox();
        e.Handled = true;
    }

    private void TransformHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);
        CancelTransformBoxInteraction(commit: true);
        e.Handled = true;
    }

    private void TransformHandle_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        CancelTransformBoxInteraction(commit: false);

    private SurveyLayerTransform CalculateRotation(SurveyWorldPoint pointer)
    {
        var currentAngle = Angle(pointer.X - _rotationCenterWorld.X, pointer.Y - _rotationCenterWorld.Y);
        var rotation = NormalizeDegrees(_transformStart.RotationDegrees + currentAngle - _rotationStartAngle);
        var radians = rotation * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scaledCenterX = (_transformLayerWidth / 2d) * _transformStart.ScaleX;
        var scaledCenterY = (_transformLayerHeight / 2d) * _transformStart.ScaleY;
        return _transformStart with
        {
            RotationDegrees = rotation,
            TranslationX = _rotationCenterWorld.X - ((scaledCenterX * cosine) - (scaledCenterY * sine)),
            TranslationY = _rotationCenterWorld.Y - ((scaledCenterX * sine) + (scaledCenterY * cosine))
        };
    }

    private SurveyLayerTransform CalculateScale(
        TransformHandle handle,
        SurveyWorldPoint pointer,
        bool freeCornerScale)
    {
        var radians = -_transformStart.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var dx = pointer.X - _transformFixedWorld.X;
        var dy = pointer.Y - _transformFixedWorld.Y;
        var localDeltaX = (dx * cosine) - (dy * sine);
        var localDeltaY = (dx * sine) + (dy * cosine);
        var active = ActiveAnchor(handle, _transformLayerWidth, _transformLayerHeight);
        var baseX = (active.X - _transformFixedLocal.X) * _transformStart.ScaleX;
        var baseY = (active.Y - _transformFixedLocal.Y) * _transformStart.ScaleY;
        var scaleX = _transformStart.ScaleX;
        var scaleY = _transformStart.ScaleY;

        if (IsCornerHandle(handle) && !freeCornerScale)
        {
            var denominator = (baseX * baseX) + (baseY * baseY);
            var ratio = denominator <= double.Epsilon
                ? 1d
                : ((localDeltaX * baseX) + (localDeltaY * baseY)) / denominator;
            var minimumRatio = Math.Max(
                MinimumTransformScale / _transformStart.ScaleX,
                MinimumTransformScale / _transformStart.ScaleY);
            ratio = Math.Max(minimumRatio, ratio);
            scaleX = _transformStart.ScaleX * ratio;
            scaleY = _transformStart.ScaleY * ratio;
        }
        else
        {
            if (Math.Abs(active.X - _transformFixedLocal.X) > double.Epsilon)
                scaleX = Math.Max(MinimumTransformScale,
                    localDeltaX / (active.X - _transformFixedLocal.X));
            if (Math.Abs(active.Y - _transformFixedLocal.Y) > double.Epsilon)
                scaleY = Math.Max(MinimumTransformScale,
                    localDeltaY / (active.Y - _transformFixedLocal.Y));
        }

        return KeepAnchorFixed(_transformStart with { ScaleX = scaleX, ScaleY = scaleY });
    }

    private SurveyLayerTransform KeepAnchorFixed(SurveyLayerTransform transform)
    {
        var radians = transform.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scaledX = _transformFixedLocal.X * transform.ScaleX;
        var scaledY = _transformFixedLocal.Y * transform.ScaleY;
        return transform with
        {
            TranslationX = _transformFixedWorld.X - ((scaledX * cosine) - (scaledY * sine)),
            TranslationY = _transformFixedWorld.Y - ((scaledX * sine) + (scaledY * cosine))
        };
    }

    private void ApplyLiveTransform(SurveyLayerTransform transform)
    {
        if (_dragVisualTransform is null)
            return;
        _dragVisualTransform.ScaleX = transform.ScaleX;
        _dragVisualTransform.ScaleY = transform.ScaleY;
        _dragVisualTransform.Rotation = transform.RotationDegrees;
        _dragVisualTransform.TranslateX = transform.TranslationX + _originX;
        _dragVisualTransform.TranslateY = transform.TranslationY + _originY;
    }

    private void CancelTransformBoxInteraction(bool commit)
    {
        if (_activeTransformHandle is null)
            return;
        var layerId = _dragLayerId;
        if (!commit)
        {
            _dragTransform = _transformStart;
            ApplyLiveTransform(_transformStart);
        }
        _activeTransformHandle = null;
        _dragLayerId = null;
        _dragVisualTransform = null;
        UpdateTransformBox();
        if (commit && layerId is { } id)
        {
            TransformCommitted?.Invoke(this, new SurveyLayerTransformEventArgs
            {
                LayerId = id,
                Transform = _dragTransform
            });
        }
    }

    private void DisposeTransformBox()
    {
        CancelTransformBoxInteraction(commit: false);
        foreach (var visual in _transformHandles.Values)
        {
            visual.PointerPressed -= TransformHandle_PointerPressed;
            visual.PointerMoved -= TransformHandle_PointerMoved;
            visual.PointerReleased -= TransformHandle_PointerReleased;
            visual.PointerCanceled -= TransformHandle_PointerCanceled;
        }
        _transformHandles.Clear();
        _transformBox.Children.Clear();
    }

    private static SurveyWorldPoint ActiveAnchor(TransformHandle handle, double width, double height) => handle switch
    {
        TransformHandle.TopLeft => new(0d, 0d),
        TransformHandle.Top => new(width / 2d, 0d),
        TransformHandle.TopRight => new(width, 0d),
        TransformHandle.Right => new(width, height / 2d),
        TransformHandle.BottomRight => new(width, height),
        TransformHandle.Bottom => new(width / 2d, height),
        TransformHandle.BottomLeft => new(0d, height),
        TransformHandle.Left => new(0d, height / 2d),
        _ => new(width / 2d, height / 2d)
    };

    private static SurveyWorldPoint OppositeAnchor(TransformHandle handle, double width, double height) => handle switch
    {
        TransformHandle.TopLeft => new(width, height),
        TransformHandle.Top => new(width / 2d, height),
        TransformHandle.TopRight => new(0d, height),
        TransformHandle.Right => new(0d, height / 2d),
        TransformHandle.BottomRight => new(0d, 0d),
        TransformHandle.Bottom => new(width / 2d, 0d),
        TransformHandle.BottomLeft => new(width, 0d),
        TransformHandle.Left => new(width, height / 2d),
        _ => new(width / 2d, height / 2d)
    };

    private static bool IsCornerHandle(TransformHandle handle) => handle is
        TransformHandle.TopLeft or TransformHandle.TopRight
        or TransformHandle.BottomRight or TransformHandle.BottomLeft;

    private static bool IsRotationHandle(TransformHandle handle) => handle is
        TransformHandle.RotateTopLeft or TransformHandle.RotateTopRight
        or TransformHandle.RotateBottomRight or TransformHandle.RotateBottomLeft;

    private static Point Midpoint(Point left, Point right) =>
        new((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static Point OffsetFromCenter(Point point, Point center, double distance)
    {
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var length = Math.Max(0.000001d, Math.Sqrt((dx * dx) + (dy * dy)));
        return new Point(point.X + (dx / length * distance), point.Y + (dy / length * distance));
    }

    private static double Angle(double x, double y) => Math.Atan2(y, x) * 180d / Math.PI;

    private static double NormalizeDegrees(double value)
    {
        value %= 360d;
        if (value <= -180d)
            value += 360d;
        else if (value > 180d)
            value -= 360d;
        return value;
    }
}
