using IDVBuff.Features.Maps;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private EditorSelection? HitModernElement(NormalizedPoint point)
    {
        var profile = GetActiveFloorProfile();
        for (var index = profile.Annotations.Count - 1; index >= 0; index--)
        {
            var annotation = profile.Annotations[index];
            if (!annotation.IsValid || !IsModernItemVisible("graphics", ModernAnnotationKey(annotation.Id)))
                continue;
            if (annotation.Type == MapAnnotationType.Line)
            {
                if (annotation.Start is not null && annotation.End is not null
                    && ModernDistanceToLinePixels(point, annotation.Start, annotation.End) <= 8 / ModernZoomFactor)
                    return new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
            }
            else if (annotation.Bounds is not null && ModernRectangleContains(annotation.Bounds, point, 5))
            {
                return new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
            }
        }
        for (var index = profile.Anchors.Count - 1; index >= 0; index--)
        {
            var anchor = profile.Anchors[index];
            if (anchor.Bounds?.IsValid is true
                && IsModernItemVisible("special", ModernAnchorKey(anchor.Id))
                && ModernRectangleContains(ToModernSourceBounds(anchor.Bounds), point, 5))
                return new EditorSelection(EditorSelectionKind.Anchor, anchor.Id);
        }
        if (profile.RecognitionRegion?.IsValid is true && IsModernItemVisible("special", "crop")
            && ModernRectangleBorderContains(profile.RecognitionRegion, point, 8))
            return new EditorSelection(EditorSelectionKind.Crop);
        return null;
    }

    private string? HitModernSelectionHandle(Point pixelPoint)
    {
        if (_modernSelection is null || _modernCanvas is null)
            return null;
        var radius = 10 / ModernZoomFactor;
        if (_modernSelection.Kind == EditorSelectionKind.Annotation
            && FindModernSelectedAnnotation() is { Type: MapAnnotationType.Line } line
            && line.Start is not null && line.End is not null)
        {
            if (ModernPixelDistance(pixelPoint, line.Start) <= radius)
                return "start";
            if (ModernPixelDistance(pixelPoint, line.End) <= radius)
                return "end";
            return null;
        }
        var bounds = GetModernSelectionSourceBounds();
        if (bounds?.IsValid is not true)
            return null;
        var pixels = ToModernPixelRect(bounds);
        foreach (var (handle, point) in new[]
        {
            ("nw", new Point(pixels.Left, pixels.Top)),
            ("n", new Point(pixels.Left + pixels.Width / 2, pixels.Top)),
            ("ne", new Point(pixels.Right, pixels.Top)),
            ("e", new Point(pixels.Right, pixels.Top + pixels.Height / 2)),
            ("se", new Point(pixels.Right, pixels.Bottom)),
            ("s", new Point(pixels.Left + pixels.Width / 2, pixels.Bottom)),
            ("sw", new Point(pixels.Left, pixels.Bottom)),
            ("w", new Point(pixels.Left, pixels.Top + pixels.Height / 2))
        })
        {
            if (Math.Sqrt(Math.Pow(pixelPoint.X - point.X, 2) + Math.Pow(pixelPoint.Y - point.Y, 2)) <= radius)
                return handle;
        }
        return null;
    }

    private void CaptureModernOriginalGeometry()
    {
        _modernOriginalBounds = GetModernSelectionSourceBounds()?.Clone();
        var annotation = FindModernSelectedAnnotation();
        _modernOriginalStart = annotation?.Start?.Clone();
        _modernOriginalEnd = annotation?.End?.Clone();
        if (_modernSelection?.Kind == EditorSelectionKind.Crop)
            _modernPendingBounds = _modernOriginalBounds?.Clone();
    }

    private void MoveModernSelection(NormalizedPoint current)
    {
        var start = ToModernNormalizedPoint(_modernPointerStart, true);
        if (start is null)
            return;
        var deltaX = current.X - start.X;
        var deltaY = current.Y - start.Y;
        if (_modernOriginalStart is not null && _modernOriginalEnd is not null)
        {
            deltaX = Math.Clamp(deltaX, -Math.Min(_modernOriginalStart.X, _modernOriginalEnd.X),
                1 - Math.Max(_modernOriginalStart.X, _modernOriginalEnd.X));
            deltaY = Math.Clamp(deltaY, -Math.Min(_modernOriginalStart.Y, _modernOriginalEnd.Y),
                1 - Math.Max(_modernOriginalStart.Y, _modernOriginalEnd.Y));
            ApplyModernSelectedLine(
                new NormalizedPoint { X = _modernOriginalStart.X + deltaX, Y = _modernOriginalStart.Y + deltaY },
                new NormalizedPoint { X = _modernOriginalEnd.X + deltaX, Y = _modernOriginalEnd.Y + deltaY });
            return;
        }
        if (_modernOriginalBounds?.IsValid is not true)
            return;
        var moved = _modernOriginalBounds.Clone();
        moved.X = Math.Clamp(moved.X + deltaX, 0, 1 - moved.Width);
        moved.Y = Math.Clamp(moved.Y + deltaY, 0, 1 - moved.Height);
        ApplyModernSelectedBounds(moved);
    }

    private void ResizeModernSelection(NormalizedPoint current)
    {
        if (_modernOriginalStart is not null && _modernOriginalEnd is not null)
        {
            if (_modernResizeHandle == "start")
                ApplyModernSelectedLine(current, _modernOriginalEnd.Clone());
            else
                ApplyModernSelectedLine(_modernOriginalStart.Clone(), current);
            return;
        }
        if (_modernOriginalBounds?.IsValid is not true)
            return;
        var original = _modernOriginalBounds;
        var left = original.X;
        var top = original.Y;
        var right = original.X + original.Width;
        var bottom = original.Y + original.Height;
        var minWidth = 4 / Math.Max(1, (_modernCanvas?.Width ?? 1) * ModernZoomFactor);
        var minHeight = 4 / Math.Max(1, (_modernCanvas?.Height ?? 1) * ModernZoomFactor);
        if (_modernResizeHandle.Contains('w')) left = Math.Min(current.X, right - minWidth);
        if (_modernResizeHandle.Contains('e')) right = Math.Max(current.X, left + minWidth);
        if (_modernResizeHandle.Contains('n')) top = Math.Min(current.Y, bottom - minHeight);
        if (_modernResizeHandle.Contains('s')) bottom = Math.Max(current.Y, top + minHeight);
        ApplyModernSelectedBounds(new NormalizedRectangle
        {
            X = Math.Clamp(left, 0, 1),
            Y = Math.Clamp(top, 0, 1),
            Width = Math.Clamp(right, 0, 1) - Math.Clamp(left, 0, 1),
            Height = Math.Clamp(bottom, 0, 1) - Math.Clamp(top, 0, 1)
        });
    }

    private void ApplyModernSelectedBounds(NormalizedRectangle bounds)
    {
        if (_modernSelection?.Kind == EditorSelectionKind.Crop)
        {
            _modernPendingBounds = bounds;
            return;
        }
        if (_modernSelection?.Kind == EditorSelectionKind.Annotation)
        {
            var annotation = FindModernSelectedAnnotation();
            if (annotation is not null)
                annotation.Bounds = bounds;
            return;
        }
        var anchor = FindModernSelectedAnchor();
        var recognitionBounds = ToModernRecognitionBounds(bounds);
        if (anchor is not null && recognitionBounds?.IsValid is true)
            anchor.Bounds = recognitionBounds;
    }

    private void ApplyModernSelectedLine(NormalizedPoint start, NormalizedPoint end)
    {
        var annotation = FindModernSelectedAnnotation();
        if (annotation?.Type != MapAnnotationType.Line)
            return;
        annotation.Start = start;
        annotation.End = end;
    }

    private void CommitModernSelectionTransform()
    {
        if (_modernSelection?.Kind == EditorSelectionKind.Crop && _modernPendingBounds?.IsValid is true)
            MapRecognitionCoordinates.ApplyRecognitionRegion(GetActiveFloorProfile(), _modernPendingBounds);
        _modernPendingBounds = null;
        UpdateMarkerConfirmState();
    }

    private void CancelModernInteraction(bool restoreGeometry)
    {
        if (restoreGeometry && _modernInteraction is EditorInteractionKind.Move or EditorInteractionKind.Resize)
        {
            if (_modernOriginalStart is not null && _modernOriginalEnd is not null)
                ApplyModernSelectedLine(_modernOriginalStart, _modernOriginalEnd);
            else if (_modernOriginalBounds?.IsValid is true && _modernSelection?.Kind != EditorSelectionKind.Crop)
                ApplyModernSelectedBounds(_modernOriginalBounds);
        }
        _modernInteraction = EditorInteractionKind.None;
        _modernPendingBounds = null;
        _modernPendingStart = null;
        _modernPendingEnd = null;
        _modernCapturedPointerId = null;
        ClearModernOriginalGeometry();
        RenderModernEditor();
    }

    private void ClearModernOriginalGeometry()
    {
        _modernOriginalBounds = null;
        _modernOriginalStart = null;
        _modernOriginalEnd = null;
        _modernResizeHandle = string.Empty;
    }

    private void DeleteModernSelection()
    {
        if (_modernSelection is null)
            return;
        var profile = GetActiveFloorProfile();
        if (_modernSelection.Kind == EditorSelectionKind.Annotation && _modernSelection.Id is { } annotationId)
            profile.Annotations.RemoveAll(annotation => annotation.Id == annotationId);
        else if (_modernSelection.Kind == EditorSelectionKind.Crop)
        {
            MapRecognitionCoordinates.ApplyRecognitionRegion(profile, new NormalizedRectangle { Width = 1, Height = 1 });
            profile.RecognitionRegion = null;
        }
        else if (_modernSelection.Kind == EditorSelectionKind.Anchor && FindModernSelectedAnchor() is { } anchor)
        {
            if (anchor.IsBuiltIn)
                anchor.Bounds = null;
            else
                profile.Anchors.Remove(anchor);
        }
        _modernSelection = null;
        UpdateMarkerConfirmState();
        SetModernStatus("已删除选中元素。", false);
        RefreshModernLayerList();
        RenderModernEditor();
    }

    private async Task EditModernSelectedTextAsync()
    {
        var annotation = FindModernSelectedAnnotation();
        if (annotation?.Type != MapAnnotationType.Text)
            return;
        var textBox = new TextBox { Text = annotation.Text ?? string.Empty, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改文字",
            Content = textBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(textBox.Text))
            return;
        annotation.Text = textBox.Text.Trim();
        RefreshModernLayerList();
        RenderModernEditor();
    }

    private bool HandleModernMarkerEditorKeyDown(KeyRoutedEventArgs e)
    {
        if (!_modernEditorActive)
            return false;
        if (e.Key == VirtualKey.Z && IsModernKeyDown(VirtualKey.Control) && !IsModernTextInputFocused())
        {
            UndoLatestModernCreation();
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Delete)
        {
            DeleteModernSelection();
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Enter && _modernToolState.ActiveTool == MapEditorTool.Line
            && _editorPreferenceState.LineDefaults.Mode == MapEditorLineMode.Continuous
            && _modernContinuousLineStart is not null)
        {
            EndModernContinuousLine();
            SetModernStatus("已结束连续直线编辑。", false);
            RenderModernEditor();
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Enter && FindModernSelectedAnnotation()?.Type == MapAnnotationType.Text)
        {
            _ = EditModernSelectedTextAsync();
            e.Handled = true;
            return true;
        }
        if (e.Key != VirtualKey.Escape)
            return false;
        if (_modernInteraction != EditorInteractionKind.None)
        {
            CancelModernInteraction(restoreGeometry: true);
            SetModernStatus("已取消当前拖动。", false);
        }
        else if (_modernToolState.CancelTransient())
        {
            SetModernStatus("已取消门标记序列，原门数据未改变。", false);
            RenderModernEditor();
        }
        else if (_modernFocusMode)
        {
            ToggleModernFocusMode();
        }
        else if (_modernToolState.ActiveTool != MapEditorTool.Select)
        {
            EndModernContinuousLine();
            _modernToolState.Select(MapEditorTool.Select);
            RefreshModernToolVisuals();
            SetModernStatus(ModernToolHint(MapEditorTool.Select), false);
        }
        else
        {
            _modernSelection = null;
            RefreshModernLayerList();
            RenderModernEditor();
        }
        e.Handled = true;
        return true;
    }

    private bool IsModernTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused is TextBox or AutoSuggestBox;
    }

    private NormalizedPoint? ToModernNormalizedPoint(Point point, bool clamp)
    {
        if (_modernCanvas is null || _modernCanvas.Width <= 0 || _modernCanvas.Height <= 0)
            return null;
        if (!clamp && (point.X < 0 || point.Y < 0 || point.X > _modernCanvas.Width || point.Y > _modernCanvas.Height))
            return null;
        return new NormalizedPoint
        {
            X = Math.Clamp(point.X / _modernCanvas.Width, 0, 1),
            Y = Math.Clamp(point.Y / _modernCanvas.Height, 0, 1)
        };
    }

    private NormalizedPoint SnapModernPoint(NormalizedPoint point)
    {
        if (!_modernSnapEnabled || IsModernKeyDown(VirtualKey.Menu) || _modernCanvas is null)
            return point;
        var spacing = GetModernGridSpacingPixels();
        return new NormalizedPoint
        {
            X = Math.Clamp(Math.Round(point.X * _modernCanvas.Width / spacing) * spacing / _modernCanvas.Width, 0, 1),
            Y = Math.Clamp(Math.Round(point.Y * _modernCanvas.Height / spacing) * spacing / _modernCanvas.Height, 0, 1)
        };
    }

    private static bool IsModernKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static NormalizedRectangle CreateModernNormalizedRectangle(NormalizedPoint start, NormalizedPoint end) => new()
    {
        X = Math.Min(start.X, end.X),
        Y = Math.Min(start.Y, end.Y),
        Width = Math.Abs(end.X - start.X),
        Height = Math.Abs(end.Y - start.Y)
    };

    private bool ModernDragIsLargeEnough()
    {
        var dx = (_modernPointerCurrent.X - _modernPointerStart.X) * ModernZoomFactor;
        var dy = (_modernPointerCurrent.Y - _modernPointerStart.Y) * ModernZoomFactor;
        return Math.Sqrt(dx * dx + dy * dy) >= 4;
    }

    private bool ModernRectangleContains(NormalizedRectangle bounds, NormalizedPoint point, double toleranceDip)
    {
        var toleranceX = toleranceDip / Math.Max(1, (_modernCanvas?.Width ?? 1) * ModernZoomFactor);
        var toleranceY = toleranceDip / Math.Max(1, (_modernCanvas?.Height ?? 1) * ModernZoomFactor);
        return point.X >= bounds.X - toleranceX && point.X <= bounds.X + bounds.Width + toleranceX
            && point.Y >= bounds.Y - toleranceY && point.Y <= bounds.Y + bounds.Height + toleranceY;
    }

    private bool ModernRectangleBorderContains(NormalizedRectangle bounds, NormalizedPoint point, double toleranceDip)
    {
        if (!ModernRectangleContains(bounds, point, toleranceDip))
            return false;
        var px = point.X * (_modernCanvas?.Width ?? 1);
        var py = point.Y * (_modernCanvas?.Height ?? 1);
        var rectangle = ToModernPixelRect(bounds);
        var tolerance = toleranceDip / ModernZoomFactor;
        return Math.Abs(px - rectangle.Left) <= tolerance || Math.Abs(px - rectangle.Right) <= tolerance
            || Math.Abs(py - rectangle.Top) <= tolerance || Math.Abs(py - rectangle.Bottom) <= tolerance;
    }

    private double ModernDistanceToLinePixels(NormalizedPoint point, NormalizedPoint start, NormalizedPoint end)
    {
        var width = _modernCanvas?.Width ?? 1;
        var height = _modernCanvas?.Height ?? 1;
        var px = point.X * width;
        var py = point.Y * height;
        var sx = start.X * width;
        var sy = start.Y * height;
        var ex = end.X * width;
        var ey = end.Y * height;
        var dx = ex - sx;
        var dy = ey - sy;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= .000001)
            return Math.Sqrt(Math.Pow(px - sx, 2) + Math.Pow(py - sy, 2));
        var t = Math.Clamp(((px - sx) * dx + (py - sy) * dy) / lengthSquared, 0, 1);
        return Math.Sqrt(Math.Pow(px - (sx + t * dx), 2) + Math.Pow(py - (sy + t * dy), 2));
    }

    private double ModernPixelDistance(Point point, NormalizedPoint normalized)
    {
        var x = normalized.X * (_modernCanvas?.Width ?? 1);
        var y = normalized.Y * (_modernCanvas?.Height ?? 1);
        return Math.Sqrt(Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2));
    }
}
