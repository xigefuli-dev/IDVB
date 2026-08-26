using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void MarkerSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        if ((!_isSelectingRecognitionRegion && GetActiveAnchor() is null && !isAnnotationMode) || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint)
            : ToRecognitionNormalizedPoint(surfacePoint);
        if (point is null)
            return;

        _dragStart = point;
        _pendingMarker = new NormalizedRectangle { X = point.Value.X, Y = point.Value.Y };
        _markerSurface.CapturePointer(e.Pointer);
        RenderMarkerVisuals();
        e.Handled = true;
    }

    private void MarkerSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint, clamp: true)
            : ToRecognitionNormalizedPoint(surfacePoint, clamp: true);
        if (point is null)
            return;

        _pendingMarker = CreateNormalizedRectangle(_dragStart.Value, point.Value);
        RenderMarkerVisuals();
    }

    private void MarkerSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint, clamp: true)
            : ToRecognitionNormalizedPoint(surfacePoint, clamp: true);
        if (point is not null)
            _pendingMarker = CreateNormalizedRectangle(_dragStart.Value, point.Value);
        _markerSurface.ReleasePointerCapture(e.Pointer);
        CommitPendingMarker();
    }

    private void MarkerSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_markerSurface is not null)
            _markerSurface.ReleasePointerCapture(e.Pointer);
        _pendingMarker = null;
        _dragStart = null;
        RenderMarkerVisuals();
    }

    private void CommitPendingMarker()
    {
        if (_activeAnnotationType == MapAnnotationType.Outline
            && _pendingMarker?.IsValid is true)
        {
            GetActiveFloorProfile().Annotations.Add(new MapAnnotation
            {
                Type = MapAnnotationType.Outline,
                ColorIndex = _selectedAnnotationColor,
                Bounds = _pendingMarker.Clone()
            });
            _pendingMarker = null;
            _dragStart = null;
            _activeAnnotationType = default;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
            return;
        }

        if (_activeAnnotationType == MapAnnotationType.Text
            && _pendingMarker?.IsValid is true)
        {
            var bounds = _pendingMarker;
            _pendingMarker = null;
            _dragStart = null;
            _ = CommitTextAnnotationAsync(bounds);
            return;
        }

        if (_isSelectingRecognitionRegion)
        {
            if (_pendingMarker?.IsValid is true)
                ApplyRecognitionRegion(_pendingMarker);
        }
        else
        {
            var anchor = GetActiveAnchor();
            if (anchor is not null && _pendingMarker?.IsValid is true)
                anchor.Bounds = _pendingMarker.Clone();
        }
        _pendingMarker = null;
        _dragStart = null;
        UpdateMarkerConfirmState();
        RenderMarkerVisuals();
    }

    private void ApplyRecognitionRegion(NormalizedRectangle newRegion)
    {
        var profile = GetActiveFloorProfile();
        MapRecognitionCoordinates.ApplyRecognitionRegion(profile, newRegion);
    }

    private void RenderMarkerVisuals()
    {
        if (_markerCanvas is null || _markerSurface is null || _draft is null)
            return;
        _markerCanvas.Children.Clear();
        if (_markerSurface.ActualWidth <= 0 || _markerSurface.ActualHeight <= 0)
            return;

        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;

        if (!_isSelectingRecognitionRegion && !isAnnotationMode && GetActiveAnchor() is not null)
        {
            _markerCanvas.Children.Add(new Rectangle
            {
                Width = _markerSurface.ActualWidth,
                Height = _markerSurface.ActualHeight,
                Fill = new SolidColorBrush(Color.FromArgb(168, 0, 0, 0)),
                IsHitTestVisible = false
            });
        }

        // Dim the surface when dragging in annotation mode
        if (isAnnotationMode && _dragStart is not null)
        {
            _markerCanvas.Children.Add(new Rectangle
            {
                Width = _markerSurface.ActualWidth,
                Height = _markerSurface.ActualHeight,
                Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                IsHitTestVisible = false
            });
        }

        // Render annotation pending marker during drag
        if (isAnnotationMode && _pendingMarker?.IsValid is true)
        {
            AddMarkerRectangle(_pendingMarker, AnnotationColors[_selectedAnnotationColor],
                isSourceRelative: true, isDashed: _activeAnnotationType == MapAnnotationType.Text);
        }

        var displayedRegion = _isSelectingRecognitionRegion && _pendingMarker?.IsValid is true
            ? _pendingMarker
            : GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        AddMarkerRectangle(displayedRegion, RecognitionRegionRed, isSourceRelative: true, isDashed: true);

        var recognitionRegion = GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        foreach (var anchor in GetActiveFloorProfile().Anchors)
        {
            var bounds = anchor.Id == _activeAnchorId && _pendingMarker is not null
                ? _pendingMarker
                : anchor.Bounds;
            AddMarkerRectangle(bounds, GetAnchorColor(anchor), isSourceRelative: false, isDashed: false, recognitionRegion);
        }

        foreach (var annotation in GetActiveFloorProfile().Annotations)
        {
            if (!annotation.IsValid)
                continue;
            var color = AnnotationColors[annotation.ColorIndex];
            var isDashed = annotation.Type == MapAnnotationType.Text;
            AddMarkerRectangle(annotation.Bounds, color, isSourceRelative: true, isDashed: isDashed);
            if (annotation.Type == MapAnnotationType.Text && !string.IsNullOrWhiteSpace(annotation.Text))
            {
                AddAnnotationTextLabel(annotation.Bounds!, annotation.Text, color);
            }
        }
    }

    private void AddMarkerRectangle(
        NormalizedRectangle? marker,
        Color color,
        bool isSourceRelative,
        bool isDashed,
        NormalizedRectangle? recognitionRegion = null)
    {
        if (marker?.IsValid is not true || _markerCanvas is null)
            return;
        var visible = GetVisibleImageBounds();
        var sourceMarker = isSourceRelative
            ? marker
            : ToSourceRectangle(marker, recognitionRegion ?? GetActiveFloorProfile().GetEffectiveRecognitionRegion());
        var thickness = isDashed ? 3d : 5d;
        var left = visible.X + sourceMarker.X * visible.Width;
        var top = visible.Y + sourceMarker.Y * visible.Height;
        var width = sourceMarker.Width * visible.Width;
        var height = sourceMarker.Height * visible.Height;
        if (isDashed)
        {
            var halfStroke = thickness / 2d;
            if (sourceMarker.X <= 0.000001d)
            {
                left += halfStroke;
                width -= halfStroke;
            }
            if (sourceMarker.Y <= 0.000001d)
            {
                top += halfStroke;
                height -= halfStroke;
            }
            if (sourceMarker.X + sourceMarker.Width >= 0.999999d)
                width -= halfStroke;
            if (sourceMarker.Y + sourceMarker.Height >= 0.999999d)
                height -= halfStroke;
        }
        var rectangle = new Rectangle
        {
            Width = Math.Max(0d, width),
            Height = Math.Max(0d, height),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        if (isDashed)
            rectangle.StrokeDashArray = new DoubleCollection { 7d, 5d };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        _markerCanvas.Children.Add(rectangle);
    }

    private void AddAnnotationTextLabel(NormalizedRectangle bounds, string text, Color color)
    {
        if (_markerCanvas is null || _markerSurface is null || string.IsNullOrWhiteSpace(text))
            return;
        var visible = GetVisibleImageBounds();
        if (visible.Width <= 0 || visible.Height <= 0)
            return;
        var left = visible.X + bounds.X * visible.Width;
        var top = visible.Y + bounds.Y * visible.Height;
        var width = bounds.Width * visible.Width;
        var height = bounds.Height * visible.Height;
        if (width <= 0 || height <= 0)
            return;

        var label = new TextBlock
        {
            Text = text,
            FontSize = CalculateFittingFontSize(text, width, height),
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = width,
            Height = height,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        _markerCanvas.Children.Add(label);
    }

    /// <summary>
    /// Picks the largest font size that keeps <paramref name="text"/> inside the given
    /// pixel width and height.  CJK characters are roughly square, so we budget ~0.85
    /// of the character height per em and scale down when the text would overflow
    /// horizontally.
    /// </summary>
    private static double CalculateFittingFontSize(string text, double pixelWidth, double pixelHeight)
    {
        if (string.IsNullOrEmpty(text) || pixelWidth <= 0 || pixelHeight <= 0)
            return 8;

        var maxByHeight = pixelHeight * 0.85;
        // Each CJK character is assumed to occupy ~0.82em in width at the chosen size.
        var maxByWidth = pixelWidth / (text.Length * 0.82);
        var fontSize = Math.Min(maxByHeight, maxByWidth);
        return Math.Clamp(fontSize, 8, Math.Min(pixelHeight, 48));
    }

    private Point? ToSourceNormalizedPoint(Point point, bool clamp = false) =>
        ToNormalizedPoint(point, GetVisibleImageBounds(), clamp);

    private Point? ToRecognitionNormalizedPoint(Point point, bool clamp = false) =>
        ToNormalizedPoint(point, GetVisibleRecognitionBounds(), clamp);

    private static Point? ToNormalizedPoint(Point point, Rect bounds, bool clamp)
    {
        if (bounds.Width <= 0d || bounds.Height <= 0d)
            return null;
        if (!clamp
            && (point.X < bounds.X || point.Y < bounds.Y
                || point.X > bounds.X + bounds.Width || point.Y > bounds.Y + bounds.Height))
            return null;
        return new Point(
            Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1),
            Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1));
    }

    private Rect GetVisibleRecognitionBounds()
    {
        var visible = GetVisibleImageBounds();
        var region = GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        return new Rect(
            visible.X + region.X * visible.Width,
            visible.Y + region.Y * visible.Height,
            region.Width * visible.Width,
            region.Height * visible.Height);
    }

    private Rect GetVisibleImageBounds()
    {
        if (_markerSurface is null || _markerSurface.ActualWidth <= 0 || _markerSurface.ActualHeight <= 0)
            return Rect.Empty;
        var surfaceRatio = _markerSurface.ActualWidth / _markerSurface.ActualHeight;
        if (surfaceRatio > _imageAspectRatio)
        {
            var height = _markerSurface.ActualHeight;
            var width = height * _imageAspectRatio;
            return new Rect((_markerSurface.ActualWidth - width) / 2, 0, width, height);
        }

        var imageWidth = _markerSurface.ActualWidth;
        var imageHeight = imageWidth / _imageAspectRatio;
        return new Rect(0, (_markerSurface.ActualHeight - imageHeight) / 2, imageWidth, imageHeight);
    }

    private void UpdateMarkerSurfaceHeight()
    {
        if (_markerSurface is null || _markerSurface.ActualWidth <= 0 || _imageAspectRatio <= 0)
            return;
        var targetHeight = Math.Round(_markerSurface.ActualWidth / _imageAspectRatio);
        if (Math.Abs(_markerSurface.Height - targetHeight) > 1)
            _markerSurface.Height = targetHeight;
    }

    private void PositionMarkerControlPanel()
    {
        if (_markerControlPanel is null || _markerSurface is null)
            return;
        var visible = GetMarkerPanelBounds();
        if (visible.Width <= 0d || visible.Height <= 0d)
            return;
        var horizontalInset = Math.Min(MarkerPanelInset, visible.Width / 4d);
        var topInset = Math.Min(MarkerPanelTopSafeInset, visible.Height / 3d);
        var bottomInset = Math.Min(MarkerPanelInset, visible.Height / 4d);
        var maximumWidth = Math.Max(1d, visible.Width - (horizontalInset * 2d));
        var maximumHeight = Math.Max(1d, visible.Height - topInset - bottomInset);
        var targetWidth = Math.Min(MarkerPanelPreferredWidth, maximumWidth);
        if (Math.Abs(_markerControlPanel.Width - targetWidth) > 0.5d)
            _markerControlPanel.Width = targetWidth;
        _markerControlPanel.MaxHeight = maximumHeight;

        var panelWidth = Math.Min(
            _markerControlPanel.ActualWidth > 0d ? _markerControlPanel.ActualWidth : targetWidth,
            maximumWidth);
        var panelHeight = Math.Min(
            _markerControlPanel.ActualHeight > 0d ? _markerControlPanel.ActualHeight : maximumHeight,
            maximumHeight);
        var leftMinimum = visible.X + horizontalInset;
        var topMinimum = visible.Y + topInset;
        var horizontalTravel = Math.Max(0d, maximumWidth - panelWidth);
        var verticalTravel = Math.Max(0d, maximumHeight - panelHeight);
        Canvas.SetLeft(
            _markerControlPanel,
            leftMinimum + Math.Clamp(_panelPositionRatio.X, 0d, 1d) * horizontalTravel);
        Canvas.SetTop(
            _markerControlPanel,
            topMinimum + Math.Clamp(_panelPositionRatio.Y, 0d, 1d) * verticalTravel);
    }

    private void SetMarkerControlPanelPosition(Point requestedPosition)
    {
        if (_markerControlPanel is null)
            return;
        var visible = GetMarkerPanelBounds();
        if (visible.Width <= 0d || visible.Height <= 0d)
            return;
        var horizontalInset = Math.Min(MarkerPanelInset, visible.Width / 4d);
        var topInset = Math.Min(MarkerPanelTopSafeInset, visible.Height / 3d);
        var bottomInset = Math.Min(MarkerPanelInset, visible.Height / 4d);
        var maximumWidth = Math.Max(1d, visible.Width - (horizontalInset * 2d));
        var maximumHeight = Math.Max(1d, visible.Height - topInset - bottomInset);
        var panelWidth = Math.Min(_markerControlPanel.ActualWidth, maximumWidth);
        var panelHeight = Math.Min(_markerControlPanel.ActualHeight, maximumHeight);
        var leftMinimum = visible.X + horizontalInset;
        var topMinimum = visible.Y + topInset;
        var horizontalTravel = Math.Max(0d, maximumWidth - panelWidth);
        var verticalTravel = Math.Max(0d, maximumHeight - panelHeight);
        var left = Math.Clamp(requestedPosition.X, leftMinimum, leftMinimum + horizontalTravel);
        var top = Math.Clamp(requestedPosition.Y, topMinimum, topMinimum + verticalTravel);
        Canvas.SetLeft(_markerControlPanel, left);
        Canvas.SetTop(_markerControlPanel, top);
        _panelPositionRatio = new Point(
            horizontalTravel > 0d ? (left - leftMinimum) / horizontalTravel : 0d,
            verticalTravel > 0d ? (top - topMinimum) / verticalTravel : 0d);
    }

    private void MarkerPanelDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_markerSurface is null || _markerControlPanel is null || sender is not UIElement handle)
            return;
        PositionMarkerControlPanel();
        _panelDragStart = e.GetCurrentPoint(_markerSurface).Position;
        _panelDragOrigin = new Point(
            Canvas.GetLeft(_markerControlPanel),
            Canvas.GetTop(_markerControlPanel));
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panelDragStart is null || _markerSurface is null)
            return;
        var current = e.GetCurrentPoint(_markerSurface).Position;
        SetMarkerControlPanelPosition(new Point(
            _panelDragOrigin.X + current.X - _panelDragStart.Value.X,
            _panelDragOrigin.Y + current.Y - _panelDragStart.Value.Y));
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        _panelDragStart = null;
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        _panelDragStart = null;
        PositionMarkerControlPanel();
        e.Handled = true;
    }

    private Rect GetMarkerPanelBounds()
    {
        var imageBounds = GetVisibleImageBounds();
        if (imageBounds.Width <= 0d || imageBounds.Height <= 0d
            || _markerPanelCanvas is null
            || _markerHostScroller is null
            || _markerHostScroller.ActualWidth <= 0d
            || _markerHostScroller.ActualHeight <= 0d)
        {
            return imageBounds;
        }

        try
        {
            var viewportTransform = _markerHostScroller.TransformToVisual(_markerPanelCanvas);
            var topLeft = viewportTransform.TransformPoint(new Point(0d, 0d));
            var bottomRight = viewportTransform.TransformPoint(
                new Point(_markerHostScroller.ActualWidth, _markerHostScroller.ActualHeight));
            var viewportBounds = new Rect(
                Math.Min(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Abs(bottomRight.X - topLeft.X),
                Math.Abs(bottomRight.Y - topLeft.Y));
            return Intersect(imageBounds, viewportBounds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return imageBounds;
        }
    }

    private void AttachMarkerHostScroller()
    {
        if (_markerSurface is null)
            return;

        var hostScroller = FindAncestorScrollViewer(_markerSurface);
        if (ReferenceEquals(_markerHostScroller, hostScroller))
            return;

        DetachMarkerHostScroller();
        _markerHostScroller = hostScroller;
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged += MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged += MarkerHostScroller_SizeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CloseHoldPreviewImmediately();
        ResetMarkerEditorSession();
        _previewImages.Clear();
        _workflowHost.Content = null;
        DetachMarkerHostScroller();
        if (ParentScrollViewer is not null)
            ParentScrollViewer.SizeChanged -= OnParentScrollViewerSizeChanged;
    }

    private void DetachMarkerHostScroller()
    {
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged -= MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged -= MarkerHostScroller_SizeChanged;
        _markerHostScroller = null;
    }

    private void MarkerHostScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private void MarkerHostScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : Rect.Empty;
    }
}
