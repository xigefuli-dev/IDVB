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

    private async Task CommitModernContinuousLineAsync(NormalizedPoint start, NormalizedPoint end)
    {
        if (!start.IsValid || !end.IsValid || !MapAnnotationIsValidLine(start, end))
        {
            SetModernStatus("连续直线的两个端点不能重合。", true);
            return;
        }
        var profile = GetActiveFloorProfile();
        var annotation = new MapAnnotation
        {
            Type = MapAnnotationType.Line,
            ColorHex = _currentAnnotationColor,
            ColorIndex = MapAnnotationColor.ToLegacyIndex(_currentAnnotationColor),
            Start = start.Clone(),
            End = end.Clone()
        };
        profile.Annotations.Add(annotation);
        _modernSelection = new EditorSelection(EditorSelectionKind.Annotation, annotation.Id);
        _modernContinuousLineStart = end.Clone();
        RecordModernAnnotationCreation(profile, annotation, start);
        await RememberEditorColorAsync(_currentAnnotationColor);
        CompleteModernCreation("已添加连续直线段；单击以继续，按 Enter 结束。", returnToSelect: false);
    }

    private void RecordModernAnnotationCreation(
        FloorRecognitionProfile profile,
        MapAnnotation annotation,
        NormalizedPoint? continuousRestartPoint = null)
    {
        var id = annotation.Id;
        RecordModernCreation("已撤销最新创建的图形。", () =>
            profile.Annotations.RemoveAll(candidate => candidate.Id == id), continuousRestartPoint);
    }

    private void RecordModernCreation(string description, Action undo, NormalizedPoint? continuousRestartPoint = null)
    {
        _modernCreationUndoStack.Push(new ModernUndoAction(
            _activeFloorKey,
            description,
            undo,
            continuousRestartPoint?.Clone()));
    }

    private void UndoLatestModernCreation()
    {
        if (_modernCreationUndoStack.Count == 0)
        {
            SetModernStatus("没有可撤销的创建操作。", false);
            return;
        }
        var action = _modernCreationUndoStack.Pop();
        action.Undo();
        _modernSelection = null;
        _modernContinuousLineStart = _modernToolState.ActiveTool == MapEditorTool.Line
            && _editorPreferenceState.LineDefaults.Mode == MapEditorLineMode.Continuous
            && string.Equals(action.FloorKey, _activeFloorKey, StringComparison.OrdinalIgnoreCase)
            ? action.ContinuousRestartPoint?.Clone()
            : null;
        UpdateMarkerConfirmState();
        RefreshModernToolVisuals();
        RefreshModernLayerList();
        RenderModernEditor();
        SetModernStatus(action.Description, false);
    }

    private NormalizedPoint PrepareModernLineEnd(NormalizedPoint start, NormalizedPoint candidate)
    {
        var snapped = SnapModernPoint(candidate);
        var useAxisConstraint = _editorPreferenceState.LineDefaults.AxisConstraintEnabled
            || IsModernKeyDown(VirtualKey.Shift);
        return useAxisConstraint
            ? ConstrainModernLinePoint(start, snapped, _editorPreferenceState.LineDefaults.AllowDiagonalConstraint)
            : snapped;
    }

    private NormalizedPoint ConstrainModernLinePoint(NormalizedPoint start, NormalizedPoint candidate, bool allowDiagonal)
        => MapEditorLineConstraints.Apply(
            start,
            candidate,
            _modernCanvas?.Width ?? 1d,
            _modernCanvas?.Height ?? 1d,
            enabled: true,
            allowDiagonal);

    private static bool MapAnnotationIsValidLine(NormalizedPoint start, NormalizedPoint end) =>
        Math.Abs(start.X - end.X) > .000001d || Math.Abs(start.Y - end.Y) > .000001d;

    private void CommitModernFreeCrop()
    {
        if (_modernFreeCropPoints.Count < 3 || !ModernDragIsLargeEnough())
        {
            _modernFreeCropPoints.Clear();
            SetModernStatus("自由裁剪至少需要拖出一个有效区域。", true);
            RenderModernEditor();
            return;
        }
        var points = _modernFreeCropPoints.Select(point => point.Clone()).ToList();
        _modernFreeCropPoints.Clear();
        var bounds = new NormalizedRectangle
        {
            X = points.Min(point => point.X), Y = points.Min(point => point.Y),
            Width = points.Max(point => point.X) - points.Min(point => point.X),
            Height = points.Max(point => point.Y) - points.Min(point => point.Y)
        };
        if (!bounds.IsValid)
            return;
        var profile = GetActiveFloorProfile();
        var priorCrop = profile.RecognitionRegion?.Clone();
        var priorPoints = profile.FreeCropPoints.Select(point => point.Clone()).ToList();
        var priorAnchors = profile.Anchors.Select(anchor => anchor.Clone()).ToList();
        MapRecognitionCoordinates.ApplyRecognitionRegion(profile, bounds);
        profile.FreeCropPoints = points;
        RecordModernCreation("已撤销自由裁剪创建。", () =>
        {
            profile.RecognitionRegion = priorCrop?.Clone();
            profile.FreeCropPoints = priorPoints.Select(point => point.Clone()).ToList();
            profile.Anchors = priorAnchors.Select(anchor => anchor.Clone()).ToList();
        });
        _modernSelection = new EditorSelection(EditorSelectionKind.Crop);
        CompleteModernCreation("已应用自由裁剪，选区外内容不会参与识别。", returnToSelect: true);
    }

}
