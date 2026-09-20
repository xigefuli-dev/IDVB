using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Editor.WinUI;
internal sealed partial class SurveyEditorSession : IDisposable
{

    public async Task MoveLayerAsync(
        Guid layerId,
        bool towardTop,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return;
        var layer = Snapshot.Layers.Single(item => item.LayerId == layerId);
        var siblings = Snapshot.Layers
            .Where(item => item.FloorId == layer.FloorId && !item.IsDeleted)
            .OrderByDescending(item => item.ZOrder)
            .ToArray();
        var index = Array.FindIndex(siblings, item => item.LayerId == layerId);
        var adjacent = towardTop ? index - 1 : index + 1;
        if (index < 0 || adjacent < 0 || adjacent >= siblings.Length)
            return;
        var reordered = siblings.Select(item => item.LayerId).ToList();
        (reordered[index], reordered[adjacent]) = (reordered[adjacent], reordered[index]);
        if (await ApplyOrderAsync(layer.FloorId, reordered, cancellationToken))
        {
            _undo.Push(new SurveyEditorOrderHistoryEntry(
                layer.FloorId,
                siblings.Select(item => item.LayerId).ToArray(),
                reordered));
            _redo.Clear();
        }
    }

    public async Task MoveLayerBeforeAsync(
        Guid layerId,
        Guid targetLayerId,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null || layerId == targetLayerId)
            return;
        var layer = Snapshot.Layers.Single(item => item.LayerId == layerId);
        var before = Snapshot.Layers
            .Where(item => item.FloorId == layer.FloorId && !item.IsDeleted)
            .OrderByDescending(item => item.ZOrder)
            .Select(item => item.LayerId)
            .ToArray();
        var reordered = before.ToList();
        reordered.Remove(layerId);
        var targetIndex = reordered.IndexOf(targetLayerId);
        if (targetIndex < 0)
            return;
        reordered.Insert(targetIndex, layerId);
        if (before.SequenceEqual(reordered))
            return;
        if (await ApplyOrderAsync(layer.FloorId, reordered, cancellationToken))
        {
            _undo.Push(new SurveyEditorOrderHistoryEntry(layer.FloorId, before, reordered));
            _redo.Clear();
        }
    }

    public async Task UpdateMetadataAsync(
        string name,
        string mapClass,
        Guid? floorId,
        string? floorDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _coordinator.UpdateMetadataAsync(
                new SurveyProjectMetadataRequest(
                    Guid.NewGuid(),
                    ProjectId,
                    Snapshot.Project.Revision,
                    name,
                    mapClass,
                    floorId,
                    floorDisplayName),
                cancellationToken);
            if (result.Succeeded && result.Value is not null)
            {
                Snapshot = result.Value;
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (result.ErrorCode != SurveyErrorCode.RevisionConflict || attempt > 0)
            {
                Error?.Invoke(this, result.Message ?? "项目属性未能保存。");
                return;
            }
            Snapshot = await _coordinator.GetProjectAsync(ProjectId, cancellationToken);
            if (Snapshot is null)
                return;
        }
    }

    public async Task SetFloorRootAsync(
        Guid layerId,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return;
        var result = await _coordinator.EditLayerAsync(
            new SurveyLayerEditRequest(
                Guid.NewGuid(),
                ProjectId,
                layerId,
                Snapshot.Project.Revision,
                SetAsFloorRoot: true),
            cancellationToken);
        if (result.Succeeded && result.Value is not null)
        {
            Snapshot = result.Value;
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Error?.Invoke(this, result.Message ?? "楼层基准未能更新。");
        }
    }

    private async Task<bool> ApplyAsync(
        Guid layerId,
        SurveyEditorLayerState state,
        CancellationToken cancellationToken)
    {
        if (Snapshot is null)
            return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _coordinator.EditLayerAsync(
                CreateRequest(layerId, state, Snapshot.Project.Revision),
                cancellationToken);
            if (result.Succeeded && result.Value is not null)
            {
                Snapshot = result.Value;
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            if (result.ErrorCode != SurveyErrorCode.RevisionConflict || attempt > 0)
            {
                Error?.Invoke(this, result.Message ?? "图层修改未能保存。");
                return false;
            }

            Snapshot = await _coordinator.GetProjectAsync(ProjectId, cancellationToken);
            if (Snapshot is null)
            {
                Error?.Invoke(this, "测绘项目已不存在。");
                return false;
            }
        }
        return false;
    }

    private Task<bool> ApplyHistoryAsync(
        SurveyEditorHistoryEntry entry,
        bool undo,
        CancellationToken cancellationToken) => entry switch
    {
        SurveyEditorLayerHistoryEntry layer => ApplyAsync(
            layer.LayerId,
            undo ? layer.Before : layer.After,
            cancellationToken),
        SurveyEditorOrderHistoryEntry order => ApplyOrderAsync(
            order.FloorId,
            undo ? order.Before : order.After,
            cancellationToken),
        SurveyEditorBatchHistoryEntry batch => ApplyStateBatchAsync(
            undo ? batch.Before : batch.After,
            cancellationToken),
        _ => Task.FromResult(false)
    };

    private async Task<bool> ApplyStateBatchAsync(
        IReadOnlyDictionary<Guid, SurveyEditorLayerState> states,
        CancellationToken cancellationToken)
    {
        if (Snapshot is null || states.Count == 0)
            return false;
        var result = await _coordinator.ApplyLayerBatchAsync(
            new SurveyLayerBatchEditRequest(
                Guid.NewGuid(),
                ProjectId,
                Snapshot.Project.Revision,
                states.Select(pair => new SurveyLayerMutation(
                    pair.Key,
                    pair.Value.UsesCleanedDisplay,
                    pair.Value.HiddenMaskAsset,
                    ReplaceHiddenMask: true,
                    ColorFilterAsset: pair.Value.ColorFilterAsset,
                    ReplaceColorFilter: true,
                    ManualTransformOverride: pair.Value.ManualTransform,
                    ReplaceManualTransform: true,
                    ObservationState: pair.Value.ObservationState,
                    ObservationErrorCode: pair.Value.ObservationErrorCode,
                    ObservationErrorMessage: pair.Value.ObservationErrorMessage,
                    ReplaceObservationStatus: true)).ToArray()),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "批量历史状态未能恢复。");
            return false;
        }
        Snapshot = result.Value;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<bool> ApplyOrderAsync(
        Guid floorId,
        IReadOnlyList<Guid> orderedLayerIds,
        CancellationToken cancellationToken)
    {
        if (Snapshot is null)
            return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _coordinator.ReorderLayersAsync(
                new SurveyLayerOrderRequest(
                    Guid.NewGuid(),
                    ProjectId,
                    floorId,
                    Snapshot.Project.Revision,
                    orderedLayerIds),
                cancellationToken);
            if (result.Succeeded && result.Value is not null)
            {
                Snapshot = result.Value;
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            if (result.ErrorCode != SurveyErrorCode.RevisionConflict || attempt > 0)
            {
                Error?.Invoke(this, result.Message ?? "图层顺序未能保存。");
                return false;
            }
            Snapshot = await _coordinator.GetProjectAsync(ProjectId, cancellationToken);
            if (Snapshot is null)
                return false;
        }
        return false;
    }

    private SurveyLayerEditRequest CreateRequest(
        Guid layerId,
        SurveyEditorLayerState state,
        long expectedRevision) => new(
            Guid.NewGuid(),
            ProjectId,
            layerId,
            expectedRevision,
            state.ManualTransform,
            ClearManualTransform: state.ManualTransform is null,
            state.Opacity,
            state.ZOrder,
            state.IsVisible,
            state.IsLocked,
            state.IsDeleted,
            state.Name,
            Brightness: state.Brightness);

    private Dictionary<Guid, SurveyEditorLayerState> SnapshotState(IEnumerable<Guid> layerIds)
    {
        if (Snapshot is null)
            return [];
        var ids = layerIds.ToHashSet();
        var observations = Snapshot.Observations.ToDictionary(item => item.ObservationId);
        return Snapshot.Layers
            .Where(layer => ids.Contains(layer.LayerId))
            .ToDictionary(
                layer => layer.LayerId,
                layer => SurveyEditorLayerState.FromLayer(layer, observations[layer.ObservationId]));
    }

    private static IReadOnlyDictionary<Guid, SurveyEditorLayerState> FilterState(
        IReadOnlyDictionary<Guid, SurveyEditorLayerState> source,
        IEnumerable<Guid> layerIds)
    {
        var ids = layerIds.ToHashSet();
        return source.Where(pair => ids.Contains(pair.Key)).ToDictionary();
    }

    private void PushBatchHistory(
        IReadOnlyDictionary<Guid, SurveyEditorLayerState> before,
        IReadOnlyDictionary<Guid, SurveyEditorLayerState> after)
    {
        if (before.Count == 0 || before.Count != after.Count
            || before.All(pair => after.TryGetValue(pair.Key, out var state) && state == pair.Value))
            return;
        _undo.Push(new SurveyEditorBatchHistoryEntry(before, after));
        _redo.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_renderCacheGate)
            _renderCache.Clear();
        _undo.Clear();
        _redo.Clear();
        Snapshot = null;
        SnapshotChanged = null;
        Error = null;
    }
}
