using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed class SurveyEditorSession : IDisposable
{
    private readonly ISurveyCoordinator _coordinator;
    private readonly Stack<SurveyEditorHistoryEntry> _undo = [];
    private readonly Stack<SurveyEditorHistoryEntry> _redo = [];
    private readonly object _renderCacheGate = new();
    private readonly Dictionary<(Guid LayerId, string ContentKey), byte[]> _renderCache = [];
    private bool _disposed;

    public SurveyEditorSession(ISurveyCoordinator coordinator, Guid projectId)
    {
        _coordinator = coordinator;
        ProjectId = projectId;
    }

    public Guid ProjectId { get; }
    public SurveyProjectSnapshot? Snapshot { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event EventHandler? SnapshotChanged;
    public event EventHandler<string>? Error;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Snapshot = await _coordinator.GetProjectAsync(ProjectId, cancellationToken)
            ?? throw new InvalidOperationException("测绘项目不存在或已被移除。");
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<Stream> OpenAssetAsync(
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default) =>
        _coordinator.OpenAssetAsync(ProjectId, asset, cancellationToken);

    public async Task<SurveySampledPixel?> SampleRenderedPixelAsync(
        Guid layerId,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        await using var rendered = await OpenRenderedLayerAsync(layerId, cancellationToken);
        return await SurveyBitmapLoader.ReadPixelAsync(rendered, x, y, cancellationToken);
    }

    public async Task<SurveySampledPixel?> SampleCompositedPixelAsync(
        string floorKey,
        SurveyWorldPoint worldPoint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = Snapshot
            ?? throw new InvalidOperationException("测绘项目尚未加载。");
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor is null)
            return null;

        var observations = snapshot.Observations.ToDictionary(item => item.ObservationId);
        var layers = snapshot.Layers
            .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted && item.IsVisible)
            .OrderByDescending(item => item.ZOrder)
            .ToArray();
        var samples = new List<SurveyCompositeLayerPixel>(layers.Length);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!observations.TryGetValue(layer.ObservationId, out var observation))
                continue;

            var width = observation.SourceAsset.PixelWidth;
            var height = observation.SourceAsset.PixelHeight;
            SurveyRasterPixel? pixel = null;
            if (layer.EffectiveTransform.IsValid && layer.Opacity > 0d)
            {
                var local = layer.EffectiveTransform.InverseTransform(worldPoint);
                if (local.X >= 0d && local.Y >= 0d
                    && local.X < width && local.Y < height)
                {
                    var sampled = await SampleRenderedPixelAsync(
                        layer.LayerId,
                        (int)Math.Floor(local.X),
                        (int)Math.Floor(local.Y),
                        cancellationToken).ConfigureAwait(false);
                    if (sampled is { } value)
                        pixel = new SurveyRasterPixel(value.R, value.G, value.B, value.A);
                }
            }
            samples.Add(new SurveyCompositeLayerPixel(
                layer.ZOrder,
                layer.IsVisible,
                layer.IsDeleted,
                layer.Opacity,
                layer.EffectiveTransform,
                width,
                height,
                pixel));
        }
        var composite = SurveyCompositePixelSampler.Composite(worldPoint, samples);
        return composite is { } result
            ? new SurveySampledPixel(result.R, result.G, result.B, result.A)
            : null;
    }

    public async Task<Stream> OpenRenderedLayerAsync(
        Guid layerId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = Snapshot
            ?? throw new InvalidOperationException("测绘项目尚未加载。");
        var layer = snapshot.Layers.Single(item => item.LayerId == layerId);
        var observation = snapshot.Observations.Single(item => item.ObservationId == layer.ObservationId);
        var displayAsset = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
        var key = (layerId, $"{displayAsset.Sha256}:{layer.HiddenMaskAsset?.Sha256}:{layer.Brightness:R}");
        lock (_renderCacheGate)
        {
            if (_renderCache.TryGetValue(key, out var cached))
                return new MemoryStream(cached, writable: false);
        }

        await using var rendered = await _coordinator.OpenRenderedLayerAsync(
            ProjectId,
            layer,
            observation,
            cancellationToken);
        using var memory = new MemoryStream();
        await rendered.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        lock (_renderCacheGate)
        {
            if (_renderCache.Count > Math.Max(16, snapshot.Layers.Count * 2))
                _renderCache.Clear();
            _renderCache[key] = bytes;
        }
        return new MemoryStream(bytes, writable: false);
    }

    public async Task ToggleDecontaminationAsync(
        Guid layerId,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return;
        var before = SnapshotState([layerId]);
        var result = await _coordinator.ToggleLayerDecontaminationAsync(
            new SurveyLayerDecontaminationRequest(
                Guid.NewGuid(), ProjectId, layerId, Snapshot.Project.Revision),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "图层去污失败。");
            return;
        }
        Snapshot = result.Value;
        PushBatchHistory(before, SnapshotState([layerId]));
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SurveyLayerOperationResult?> AlignLayersAsync(
        IReadOnlyList<Guid> layerIds,
        Guid anchorLayerId,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return null;
        var before = SnapshotState(layerIds);
        var result = await _coordinator.AlignLayersAsync(
            new SurveyLayerAlignmentRequest(
                Guid.NewGuid(), ProjectId, Snapshot.Project.Revision,
                anchorLayerId, layerIds),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "魔术贴对齐失败。");
            return null;
        }
        Snapshot = result.Value.Snapshot;
        var succeeded = result.Value.Items.Where(item => item.Succeeded).Select(item => item.LayerId).ToArray();
        if (succeeded.Length > 0)
            PushBatchHistory(FilterState(before, succeeded), SnapshotState(succeeded));
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> NormalizeLayerColorsAsync(
        IReadOnlyList<Guid> layerIds,
        Guid anchorLayerId,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return null;
        var before = SnapshotState(layerIds);
        var result = await _coordinator.NormalizeLayerColorsAsync(
            new SurveyLayerColorNormalizationRequest(
                Guid.NewGuid(), ProjectId, Snapshot.Project.Revision, anchorLayerId, layerIds),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "图层融色失败。");
            return null;
        }
        Snapshot = result.Value.Snapshot;
        var succeeded = result.Value.Items.Where(item => item.Succeeded).Select(item => item.LayerId).ToArray();
        if (succeeded.Length > 0)
            PushBatchHistory(FilterState(before, succeeded), SnapshotState(succeeded));
        lock (_renderCacheGate)
            _renderCache.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> ApplyColorTemplateAsync(
        IReadOnlyList<Guid> layerIds,
        IReadOnlyList<SurveyColorTemplateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var selectedLayerIds = layerIds.Distinct().ToArray();
        if (Snapshot is null || selectedLayerIds.Length == 0 || entries.Count == 0)
            return null;
        var before = SnapshotState(selectedLayerIds);
        var result = await _coordinator.ApplyColorTemplateAsync(
            new SurveyLayerColorTemplateRequest(
                Guid.NewGuid(),
                ProjectId,
                Snapshot.Project.Revision,
                selectedLayerIds,
                entries),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "The color template could not be applied.");
            return null;
        }
        Snapshot = result.Value.Snapshot;
        var succeeded = result.Value.Items
            .Where(item => item.Succeeded)
            .Select(item => item.LayerId)
            .ToArray();
        if (succeeded.Length > 0)
            PushBatchHistory(FilterState(before, succeeded), SnapshotState(succeeded));
        lock (_renderCacheGate)
            _renderCache.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> CorrectVignetteAsync(
        IReadOnlyList<Guid> layerIds,
        double compensationStart,
        double compensationStrength,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null || layerIds.Count == 0)
            return null;
        var before = SnapshotState(layerIds);
        var result = await _coordinator.CorrectLayerVignetteAsync(
            new SurveyLayerVignetteCorrectionRequest(
                Guid.NewGuid(),
                ProjectId,
                Snapshot.Project.Revision,
                layerIds,
                compensationStart,
                compensationStrength),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "晕影校正失败。");
            return null;
        }
        Snapshot = result.Value.Snapshot;
        var succeeded = result.Value.Items.Where(item => item.Succeeded).Select(item => item.LayerId).ToArray();
        if (succeeded.Length > 0)
            PushBatchHistory(FilterState(before, succeeded), SnapshotState(succeeded));
        lock (_renderCacheGate)
            _renderCache.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> ApplyMaskStrokeAsync(
        Guid floorId,
        IReadOnlyList<Guid> layerIds,
        IReadOnlyList<SurveyWorldPoint> points,
        double size,
        SurveyBrushShape shape,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return null;
        var before = SnapshotState(layerIds);
        var result = await _coordinator.ApplyMaskStrokeAsync(
            new SurveyMaskStrokeRequest(
                Guid.NewGuid(), ProjectId, Snapshot.Project.Revision,
                floorId, layerIds, points, size, shape),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            Error?.Invoke(this, result.Message ?? "隐藏蒙版保存失败。");
            return null;
        }
        Snapshot = result.Value.Snapshot;
        var succeeded = result.Value.Items.Where(item => item.Succeeded).Select(item => item.LayerId).ToArray();
        if (succeeded.Length > 0)
            PushBatchHistory(FilterState(before, succeeded), SnapshotState(succeeded));
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> ApplyColorBrushAsync(
        Guid layerId, IReadOnlyList<SurveyWorldPoint> points, double size, SurveyBrushShape shape,
        SurveyColor color, CancellationToken cancellationToken = default)
    {
        if (Snapshot is null || points.Count == 0) return null;
        var before = SnapshotState([layerId]);
        var result = await _coordinator.ApplyColorBrushAsync(new SurveyColorBrushRequest(Guid.NewGuid(), ProjectId,
            layerId, Snapshot.Project.Revision, points, size, shape, color), cancellationToken);
        if (!result.Succeeded || result.Value is null) { Error?.Invoke(this, result.Message ?? "画笔保存失败。"); return null; }
        Snapshot = result.Value.Snapshot;
        if (result.Value.Items.Any(item => item.Succeeded)) PushBatchHistory(before, SnapshotState([layerId]));
        lock (_renderCacheGate) _renderCache.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyLayerOperationResult?> ApplyColorFillAsync(
        Guid layerId, int pixelX, int pixelY, byte tolerance, SurveyColor color,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null) return null;
        var before = SnapshotState([layerId]);
        var result = await _coordinator.ApplyColorFillAsync(new SurveyColorFillRequest(Guid.NewGuid(), ProjectId,
            layerId, Snapshot.Project.Revision, pixelX, pixelY, tolerance, color), cancellationToken);
        if (!result.Succeeded || result.Value is null) { Error?.Invoke(this, result.Message ?? "颜料桶保存失败。"); return null; }
        Snapshot = result.Value.Snapshot;
        if (result.Value.Items.Any(item => item.Succeeded)) PushBatchHistory(before, SnapshotState([layerId]));
        lock (_renderCacheGate) _renderCache.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    public async Task<SurveyObservationCommitResult?> ImportObservationAsync(
        byte[] bytes,
        string fileExtension,
        string mediaType,
        int pixelWidth,
        int pixelHeight,
        string floorKey,
        string? layerName = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Snapshot is null)
            return null;
        var capture = new SurveyCaptureContext(
            Guid.Empty,
            0L,
            0L,
            DateTimeOffset.UtcNow,
            pixelWidth,
            pixelHeight,
            96d,
            new SurveyPixelRect(0, 0, pixelWidth, pixelHeight),
            // EnsureFloorAsync 以原样 key 插库且 UNIQUE(project_id, floor_key)
            // 在 SQLite BINARY 排序下大小写敏感，必须归一化小写（与游戏路径一致）。
            floorKey.Trim().ToLowerInvariant(),
            Snapshot.Project.ConfigDigest,
            Snapshot.Project.AlgorithmVersion);
        var frame = new SurveyEncodedFrame(bytes, fileExtension, mediaType, pixelWidth, pixelHeight, capture);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _coordinator.ImportObservationAsync(
                new SurveyObservationImportRequest(
                    Guid.NewGuid(),
                    ProjectId,
                    Snapshot.Project.Revision,
                    frame,
                    layerName),
                cancellationToken);
            if (result.Succeeded && result.Value is not null)
            {
                Snapshot = result.Value.Snapshot;
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
                return result.Value;
            }
            if (result.ErrorCode != SurveyErrorCode.RevisionConflict || attempt > 0)
            {
                Error?.Invoke(this, result.Message ?? "图片导入失败。");
                return null;
            }
            Snapshot = await _coordinator.GetProjectAsync(ProjectId, cancellationToken);
            if (Snapshot is null)
            {
                Error?.Invoke(this, "测绘项目已不存在。");
                return null;
            }
        }
        return null;
    }

    public async Task EditAsync(
        Guid layerId,
        Func<SurveyEditorLayerState, SurveyEditorLayerState> update,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null)
            return;
        var layer = Snapshot.Layers.Single(item => item.LayerId == layerId);
        var observation = Snapshot.Observations.Single(item => item.ObservationId == layer.ObservationId);
        var before = SurveyEditorLayerState.FromLayer(layer, observation);
        var after = update(before);
        if (after == before)
            return;
        if (await ApplyAsync(layerId, after, cancellationToken))
        {
            _undo.Push(new SurveyEditorLayerHistoryEntry(layerId, before, after));
            _redo.Clear();
        }
    }

    public async Task EditManyAsync(
        IReadOnlyCollection<Guid> layerIds,
        Func<SurveyEditorLayerState, SurveyEditorLayerState> update,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot is null || layerIds.Count == 0)
            return;
        var before = SnapshotState(layerIds);
        foreach (var layerId in layerIds)
        {
            var current = SnapshotState([layerId]).GetValueOrDefault(layerId);
            if (current is null)
                continue;
            var after = update(current);
            if (after == current)
                continue;
            var result = await _coordinator.EditLayerAsync(
                CreateRequest(layerId, after, Snapshot.Project.Revision), cancellationToken);
            if (!result.Succeeded || result.Value is null)
            {
                Error?.Invoke(this, result.Message ?? "批量修改未能保存。");
                return;
            }
            Snapshot = result.Value;
        }
        var afterStates = SnapshotState(layerIds);
        PushBatchHistory(before, afterStates);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<SurveyOperationResult<SurveyDualOutput>> RenderOutputsAsync(
        string floorKey, CancellationToken cancellationToken = default) =>
        _coordinator.RenderOutputsAsync(ProjectId, floorKey, cancellationToken);

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        if (_undo.Count == 0)
            return;
        var entry = _undo.Pop();
        if (await ApplyHistoryAsync(entry, undo: true, cancellationToken: cancellationToken))
            _redo.Push(entry);
        else
            _undo.Push(entry);
    }

    public async Task RedoAsync(CancellationToken cancellationToken = default)
    {
        if (_redo.Count == 0)
            return;
        var entry = _redo.Pop();
        if (await ApplyHistoryAsync(entry, undo: false, cancellationToken: cancellationToken))
            _undo.Push(entry);
        else
            _redo.Push(entry);
    }

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
