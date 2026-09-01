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
    private void ModernCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_modernCanvas is null || _modernViewport is null)
            return;
        var pointer = e.GetCurrentPoint(_modernCanvas);
        var temporaryPan = pointer.Properties.IsMiddleButtonPressed || IsModernKeyDown(VirtualKey.Space);
        _modernPointerStart = pointer.Position;
        _modernPointerCurrent = pointer.Position;
        _modernPointerMoved = false;

        if (temporaryPan || _modernToolState.ActiveTool == MapEditorTool.Pan)
        {
            _modernInteraction = EditorInteractionKind.Pan;
            _modernPanStart = e.GetCurrentPoint(_modernViewport).Position;
            _modernPanHorizontalOffset = _modernViewport.HorizontalOffset;
            _modernPanVerticalOffset = _modernViewport.VerticalOffset;
        }
        else if (_modernToolState.ActiveTool == MapEditorTool.Select)
        {
            var normalized = ToModernNormalizedPoint(pointer.Position, false);
            if (normalized is null)
                return;
            var handle = HitModernSelectionHandle(pointer.Position);
            if (handle is not null)
            {
                _modernResizeHandle = handle;
                _modernInteraction = EditorInteractionKind.Resize;
                CaptureModernOriginalGeometry();
            }
            else
            {
                var hit = HitModernElement(normalized);
                if (hit is null)
                {
                    _modernSelection = null;
                    RefreshModernLayerList();
                    RenderModernEditor();
                    return;
                }
                _modernSelection = hit;
                if (hit.Kind == EditorSelectionKind.Background)
                {
                    RefreshModernLayerList();
                    RenderModernEditor();
                    return;
                }
                _modernInteraction = EditorInteractionKind.Move;
                CaptureModernOriginalGeometry();
                RefreshModernLayerList();
            }
        }
        else if (_modernToolState.ActiveTool == MapEditorTool.Line
            && _editorPreferenceState.LineDefaults.Mode == MapEditorLineMode.Continuous)
        {
            var normalized = ToModernNormalizedPoint(pointer.Position, false);
            if (normalized is null)
                return;
            if (_modernContinuousLineStart is null)
            {
                _modernContinuousLineStart = SnapModernPoint(normalized);
                SetModernStatus("已设置连续直线起点；单击以添加下一段，按 Enter 结束。", false);
                RenderModernEditor();
            }
            else
            {
                var end = PrepareModernLineEnd(_modernContinuousLineStart, normalized);
                _ = CommitModernContinuousLineAsync(_modernContinuousLineStart, end);
            }
            e.Handled = true;
            return;
        }
        else if (_modernToolState.ActiveTool == MapEditorTool.Conceal)
        {
            var normalized = ToModernNormalizedPoint(pointer.Position, false);
            if (normalized is null)
                return;
            var defaults = _editorPreferenceState.ConcealDefaults;
            _modernConcealStroke.Begin(
                normalized,
                defaults.Shape,
                defaults.BrushSizePixels,
                _modernBitmap?.PixelWidth ?? (int)Math.Max(1, _modernCanvas.Width),
                _modernBitmap?.PixelHeight ?? (int)Math.Max(1, _modernCanvas.Height));
            _modernInteraction = EditorInteractionKind.Create;
            _modernConcealHoverPoint = normalized;
            RenderModernEditor();
        }
        else if (_modernToolState.ActiveTool == MapEditorTool.FreeCrop)
        {
            var normalized = ToModernNormalizedPoint(pointer.Position, false);
            if (normalized is null)
                return;
            _modernFreeCropPoints.Clear();
            _modernFreeCropPoints.Add(normalized);
            _modernInteraction = EditorInteractionKind.Create;
        }
        else
        {
            var normalized = ToModernNormalizedPoint(pointer.Position, false);
            if (normalized is null)
                return;
            _modernInteraction = EditorInteractionKind.Create;
            _modernPendingStart = SnapModernPoint(normalized);
            _modernPendingEnd = _modernPendingStart.Clone();
            _modernPendingBounds = new NormalizedRectangle
            {
                X = _modernPendingStart.X,
                Y = _modernPendingStart.Y
            };
        }

        _modernCanvas.CapturePointer(e.Pointer);
        _modernCapturedPointerId = e.Pointer.PointerId;
        e.Handled = true;
    }

    private void ModernCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_modernCanvas is null)
            return;
        var point = e.GetCurrentPoint(_modernCanvas).Position;
        _modernPointerCurrent = point;
        if (_modernInteraction == EditorInteractionKind.None)
        {
            UpdateModernConcealHover(point);
            if (_modernToolState.ActiveTool == MapEditorTool.Conceal)
                RenderModernEditor();
            return;
        }
        _modernPointerMoved |= Math.Abs(point.X - _modernPointerStart.X) > 1
            || Math.Abs(point.Y - _modernPointerStart.Y) > 1;

        if (_modernInteraction == EditorInteractionKind.Pan)
        {
            if (_modernViewport is null)
                return;
            var viewportPoint = e.GetCurrentPoint(_modernViewport).Position;
            _modernViewport.ChangeView(
                Math.Max(0, _modernPanHorizontalOffset - (viewportPoint.X - _modernPanStart.X)),
                Math.Max(0, _modernPanVerticalOffset - (viewportPoint.Y - _modernPanStart.Y)),
                null,
                true);
            e.Handled = true;
            return;
        }

        var normalized = ToModernNormalizedPoint(point, true);
        if (normalized is null)
            return;
        if (_modernToolState.ActiveTool == MapEditorTool.Conceal
            && _modernInteraction == EditorInteractionKind.Create)
        {
            _modernConcealHoverPoint = normalized;
            _modernConcealStroke.AddPoint(normalized);
            AppendModernConcealPreview();
            e.Handled = true;
            return;
        }
        if (_modernToolState.ActiveTool == MapEditorTool.FreeCrop
            && _modernInteraction == EditorInteractionKind.Create)
        {
            _modernFreeCropPoints.Add(normalized);
            RenderModernEditor();
            e.Handled = true;
            return;
        }
        normalized = SnapModernPoint(normalized);
        if (_modernInteraction == EditorInteractionKind.Create)
        {
            if (_modernToolState.ActiveTool == MapEditorTool.Line && _modernPendingStart is not null)
                normalized = PrepareModernLineEnd(_modernPendingStart, normalized);
            _modernPendingEnd = normalized;
            _modernPendingBounds = CreateModernNormalizedRectangle(_modernPendingStart!, normalized);
        }
        else if (_modernInteraction == EditorInteractionKind.Move)
        {
            MoveModernSelection(normalized);
        }
        else if (_modernInteraction == EditorInteractionKind.Resize)
        {
            ResizeModernSelection(normalized);
        }
        RenderModernEditor();
        e.Handled = true;
    }

    private void ModernCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_modernCanvas is null || _modernInteraction == EditorInteractionKind.None)
            return;
        var completedInteraction = _modernInteraction;
        if (_modernCapturedPointerId == e.Pointer.PointerId)
            _modernCanvas.ReleasePointerCapture(e.Pointer);
        _modernCapturedPointerId = null;

        if (completedInteraction == EditorInteractionKind.Create)
        {
            _modernInteraction = EditorInteractionKind.None;
            _ = CommitModernCreationAsync();
        }
        else
        {
            if (completedInteraction is EditorInteractionKind.Move or EditorInteractionKind.Resize)
                CommitModernSelectionTransform();
            _modernInteraction = EditorInteractionKind.None;
            ClearModernOriginalGeometry();
            RenderModernEditor();
            RefreshModernLayerList();
        }
        e.Handled = true;
    }

    private void ModernCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_modernCanvas is not null && _modernCapturedPointerId == e.Pointer.PointerId)
            _modernCanvas.ReleasePointerCapture(e.Pointer);
        _modernCapturedPointerId = null;
        CancelModernInteraction(restoreGeometry: true);
        e.Handled = true;
    }

    private void ModernCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_modernViewport is null || _modernCanvas is null)
            return;
        var pointer = e.GetCurrentPoint(_modernCanvas);
        if (IsModernKeyDown(VirtualKey.Control) && _modernToolState.ActiveTool == MapEditorTool.Conceal)
        {
            ApplyModernConcealBrushWheel(pointer.Properties.MouseWheelDelta);
            e.Handled = true;
            return;
        }
        var viewportPoint = e.GetCurrentPoint(_modernViewport).Position;
        var oldZoom = _modernViewport.ZoomFactor;
        var multiplier = pointer.Properties.MouseWheelDelta > 0 ? 1.12f : 1f / 1.12f;
        var newZoom = Math.Clamp(oldZoom * multiplier, .1f, 8f);
        _modernViewport.ChangeView(
            Math.Max(0, pointer.Position.X * newZoom - viewportPoint.X),
            Math.Max(0, pointer.Position.Y * newZoom - viewportPoint.Y),
            newZoom,
            false);
        e.Handled = true;
    }

    private async void ModernCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_modernCanvas is null)
            return;
        var normalized = ToModernNormalizedPoint(e.GetPosition(_modernCanvas), false);
        if (normalized is null)
            return;
        var hit = HitModernElement(normalized);
        if (hit is not { Kind: EditorSelectionKind.Annotation })
            return;
        _modernSelection = hit;
        if (FindModernSelectedAnnotation()?.Type == MapAnnotationType.Text)
            await EditModernSelectedTextAsync();
        e.Handled = true;
    }

    private async Task CommitModernCreationAsync()
    {
        var tool = _modernToolState.ActiveTool;
        var bounds = _modernPendingBounds?.Clone();
        var start = _modernPendingStart?.Clone();
        var end = _modernPendingEnd?.Clone();
        _modernPendingBounds = null;
        _modernPendingStart = null;
        _modernPendingEnd = null;

        if (tool == MapEditorTool.Conceal)
        {
            var concealLayer = _modernConcealStroke.Complete();
            if (concealLayer is null)
            {
                SetModernStatus("已取消当前遮瑕。", false);
                RenderModernEditor();
                return;
            }
            var concealProfile = GetActiveFloorProfile();
            concealProfile.BackgroundLayers.Add(concealLayer);
            _modernSelection = new EditorSelection(EditorSelectionKind.Background, concealLayer.Id);
            RecordModernCreation("已撤销最新遮瑕层。", () =>
                concealProfile.BackgroundLayers.RemoveAll(layer => layer.Id == concealLayer.Id));
            CompleteModernCreation("已创建遮瑕层。", returnToSelect: false);
            return;
        }
        if (tool == MapEditorTool.FreeCrop)
        {
            CommitModernFreeCrop();
            return;
        }

        if (!ModernDragIsLargeEnough())
        {
            SetModernStatus("拖动距离至少需要 4 DIP。", true);
            RenderModernEditor();
            return;
        }

        var profile = GetActiveFloorProfile();
        switch (tool)
        {
            case MapEditorTool.Text when bounds?.IsValid is true:
                await CreateModernTextAnnotationAsync(bounds);
                break;
            case MapEditorTool.Line when start?.IsValid is true && end?.IsValid is true:
            {
                var annotation = new MapAnnotation
                {
                    Type = MapAnnotationType.Line,
                    ColorHex = _currentAnnotationColor,
                    ColorIndex = MapAnnotationColor.ToLegacyIndex(_currentAnnotationColor),
                    Start = start,
                    End = end
                };
                if (!annotation.IsValid)
                    break;
                profile.Annotations.Add(annotation);
                _modernSelection = new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
                RecordModernAnnotationCreation(profile, annotation);
                await RememberEditorColorAsync(_currentAnnotationColor);
                CompleteModernCreation("已创建直线。", returnToSelect: false);
                break;
            }
            case MapEditorTool.Rectangle when bounds?.IsValid is true:
            {
                var annotation = new MapAnnotation
                {
                    Type = MapAnnotationType.Outline,
                    ColorHex = _currentAnnotationColor,
                    ColorIndex = MapAnnotationColor.ToLegacyIndex(_currentAnnotationColor),
                    Bounds = bounds
                };
                profile.Annotations.Add(annotation);
                _modernSelection = new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
                RecordModernAnnotationCreation(profile, annotation);
                await RememberEditorColorAsync(_currentAnnotationColor);
                CompleteModernCreation("已创建矩形。", returnToSelect: false);
                break;
            }
            case MapEditorTool.Gate when bounds?.IsValid is true:
                CommitModernGate(bounds);
                break;
            case MapEditorTool.Crop when bounds?.IsValid is true:
                var priorCrop = profile.RecognitionRegion?.Clone();
                var priorCropPoints = profile.FreeCropPoints.Select(point => point.Clone()).ToList();
                var priorCropAnchors = profile.Anchors.Select(anchor => anchor.Clone()).ToList();
                MapRecognitionCoordinates.ApplyRecognitionRegion(profile, bounds);
                profile.FreeCropPoints.Clear();
                RecordModernCreation("已撤销裁剪创建。", () =>
                {
                    profile.RecognitionRegion = priorCrop?.Clone();
                    profile.FreeCropPoints = priorCropPoints.Select(point => point.Clone()).ToList();
                    profile.Anchors = priorCropAnchors.Select(anchor => anchor.Clone()).ToList();
                });
                _modernSelection = new EditorSelection(EditorSelectionKind.Crop);
                CompleteModernCreation("已更新画布裁剪范围，范围外锚点已清除。", returnToSelect: true);
                break;
            case MapEditorTool.Anchor when bounds?.IsValid is true:
                CommitModernAnchor(bounds);
                break;
            default:
                RenderModernEditor();
                break;
        }
    }

    private async Task CreateModernTextAnnotationAsync(NormalizedRectangle bounds)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "输入文字内容",
            MinWidth = 320,
            AcceptsReturn = false
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加文字",
            Content = textBox,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(textBox.Text))
        {
            SetModernStatus("已取消添加文字。", false);
            RenderModernEditor();
            return;
        }
        var annotation = new MapAnnotation
        {
            Type = MapAnnotationType.Text,
            ColorHex = _currentAnnotationColor,
            ColorIndex = MapAnnotationColor.ToLegacyIndex(_currentAnnotationColor),
            Bounds = bounds,
            Text = textBox.Text.Trim(),
            FontFamily = string.IsNullOrWhiteSpace(_editorPreferenceState.TextDefaults.FontFamily)
                ? null
                : _editorPreferenceState.TextDefaults.FontFamily,
            FontSize = _editorPreferenceState.TextDefaults.FontSize,
            IsBold = _editorPreferenceState.TextDefaults.IsBold,
            IsItalic = _editorPreferenceState.TextDefaults.IsItalic,
            IsStrikethrough = _editorPreferenceState.TextDefaults.IsStrikethrough
        };
        GetActiveFloorProfile().Annotations.Add(annotation);
        RecordModernAnnotationCreation(GetActiveFloorProfile(), annotation);
        _modernSelection = new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
        await RememberEditorColorAsync(_currentAnnotationColor);
        CompleteModernCreation("已创建文字。", returnToSelect: false);
    }

    private void CommitModernAnchor(NormalizedRectangle sourceBounds)
    {
        var recognitionBounds = ToModernRecognitionBounds(sourceBounds);
        if (recognitionBounds?.IsValid is not true)
        {
            SetModernStatus("锚点必须完全位于当前裁剪范围内。", true);
            RenderModernEditor();
            return;
        }
        var profile = GetActiveFloorProfile();
        var previousAnchors = profile.Anchors.Select(anchor => anchor.Clone()).ToList();
        var anchor = profile.Anchors.FirstOrDefault(candidate =>
            candidate.IsBuiltIn && !candidate.IsMarked
            && candidate.Key is not "main-entrance" and not "side-entrance");
        if (anchor is null)
        {
            var number = profile.Anchors.Count(candidate => candidate.Role == RecognitionAnchorRole.Optional) + 1;
            anchor = new RecognitionAnchor
            {
                Key = $"optional-{Guid.NewGuid():N}",
                DisplayName = "辅助锚点",
                Role = RecognitionAnchorRole.Optional,
                Weight = .35,
                IsBuiltIn = false
            };
            profile.Anchors.Add(anchor);
        }
        anchor.Bounds = recognitionBounds;
        RecordModernCreation("已撤销锚点创建。", () =>
        {
            profile.Anchors = previousAnchors.Select(candidate => candidate.Clone()).ToList();
        });
        _modernSelection = new EditorSelection(EditorSelectionKind.Anchor, anchor.Id);
        CompleteModernCreation("已创建锚点，可继续拖动添加。", returnToSelect: false);
    }

    private void CompleteModernCreation(string message, bool returnToSelect)
    {
        if (returnToSelect)
            _modernToolState.CompleteCreation();
        SetModernStatus(message, false);
        UpdateMarkerConfirmState();
        RefreshModernToolVisuals();
        RefreshModernLayerList();
        RenderModernEditor();
    }

}
