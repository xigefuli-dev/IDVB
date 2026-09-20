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

    private void UpdateBrushPreviewSize()
    {
        if (_brushPreview is null)
            return;
        _brushPreview.Width = _brushSize;
        _brushPreview.Height = _brushSize;
    }

    private void PositionBrushPreview(Point point)
    {
        if (_brushPreview is null)
            return;
        Canvas.SetLeft(_brushPreview, point.X - (_brushSize / 2d));
        Canvas.SetTop(_brushPreview, point.Y - (_brushSize / 2d));
    }

    private void UpdateBrushPreviewAppearance()
    {
        if (_brushPreview is null)
            return;
        var isBrush = ActiveTool == SurveyEditorTool.Brush;
        if (_brushPreview.Fill is SolidColorBrush fill)
        {
            fill.Color = isBrush
                ? Color.FromArgb(80, _paintPreviewColor.R, _paintPreviewColor.G, _paintPreviewColor.B)
                : Color.FromArgb(38, 255, 110, 80);
        }
        if (_brushPreview.Stroke is SolidColorBrush stroke)
        {
            stroke.Color = isBrush
                ? PreviewOutlineColor(_paintPreviewColor)
                : Color.FromArgb(255, 255, 110, 80);
        }
    }

    private void UpdatePointerVisuals()
    {
        var showBrushPreview = !_temporaryNavigationActive
            && _pointerInsideCanvas
            && ActiveTool is SurveyEditorTool.Eraser or SurveyEditorTool.Brush;
        _brushPreview?.SetValue(
            VisibilityProperty,
            showBrushPreview ? Visibility.Visible : Visibility.Collapsed);
        ProtectedCursor = _temporaryNavigationActive
            ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
            : showBrushPreview
                ? null
                : ActiveTool switch
                {
                    SurveyEditorTool.Pan => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Hand),
                    SurveyEditorTool.Eraser => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Cross),
                    SurveyEditorTool.Template when _templateColorSamplerArmed
                        => Microsoft.UI.Input.InputSystemCursor.Create(
                            Microsoft.UI.Input.InputSystemCursorShape.Cross),
                    SurveyEditorTool.Brush or SurveyEditorTool.PaintBucket or SurveyEditorTool.Eyedropper
                        => Microsoft.UI.Input.InputSystemCursor.Create(
                            Microsoft.UI.Input.InputSystemCursorShape.Arrow),
                    _ => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Arrow)
                };
    }

    private bool IsInsideCanvas(Point point) =>
        point.X >= 0d && point.Y >= 0d && point.X <= _canvas.Width && point.Y <= _canvas.Height;

    private static Color PreviewOutlineColor(SurveyColor color)
    {
        var luminance = ((0.299d * color.R) + (0.587d * color.G) + (0.114d * color.B)) / 255d;
        return luminance > 0.58d
            ? Color.FromArgb(255, 5, 10, 16)
            : Color.FromArgb(255, 255, 255, 255);
    }

    private void RaiseZoomChanged() => ZoomChanged?.Invoke(
        this,
        new SurveyZoomChangedEventArgs { Percent = ZoomPercent });

    private void PreserveWorldViewportPosition(double originDeltaX, double originDeltaY)
    {
        _viewportTransform.TranslateX -= originDeltaX * _viewportTransform.ScaleX;
        _viewportTransform.TranslateY -= originDeltaY * _viewportTransform.ScaleY;
        UpdateTransformBox();
    }

    private static double Distance(SurveyWorldPoint left, SurveyWorldPoint right)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
