using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed partial class SurveyEditorSession : IDisposable
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
}
