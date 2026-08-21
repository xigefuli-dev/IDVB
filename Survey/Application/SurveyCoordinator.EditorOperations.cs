using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> ApplyLayerBatchAsync(
        SurveyLayerBatchEditRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.Revision != request.ExpectedRevision)
            {
                throw new SurveyRevisionConflictException(
                    request.ProjectId,
                    request.ExpectedRevision,
                    current.Project.Revision);
            }
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyProjectSnapshot>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");
            var snapshot = await _projects.ApplyLayerBatchAsync(request, cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> ToggleLayerDecontaminationAsync(
        SurveyLayerDecontaminationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyProjectSnapshot>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");
            var layer = current.Layers.SingleOrDefault(item => item.LayerId == request.LayerId)
                ?? throw new InvalidOperationException("测绘图层不存在。");
            if (layer.IsDeleted || layer.IsLocked)
                return Failure<SurveyProjectSnapshot>(SurveyErrorCode.InvalidState, "已删除或锁定的图层不能去污。");
            var observation = current.Observations.Single(item => item.ObservationId == layer.ObservationId);
            if (observation.DisplayAsset is not null)
            {
                var toggled = await _projects.ApplyLayerBatchAsync(
                    new SurveyLayerBatchEditRequest(
                        request.CommandId,
                        request.ProjectId,
                        request.ExpectedRevision,
                        [new SurveyLayerMutation(layer.LayerId, !layer.UsesCleanedDisplay)]),
                    cancellationToken).ConfigureAwait(false);
                return SurveyOperationResult<SurveyProjectSnapshot>.Success(toggled);
            }
            if (_preprocessor is null)
                return Failure<SurveyProjectSnapshot>(SurveyErrorCode.InvalidState, "当前运行环境没有测绘去污处理器。");

            var processed = await _preprocessor.ProcessAsync(
                new SurveyPreprocessRequest(request.ProjectId, observation),
                cancellationToken).ConfigureAwait(false);
            if (processed.DisplayAsset is null)
                return Failure<SurveyProjectSnapshot>(SurveyErrorCode.PreprocessingFailed, processed.RejectionReason ?? "图层去污失败。");
            var snapshot = await _projects.CommitProcessingAsync(
                new SurveyProcessingCommitRequest(
                    request.CommandId,
                    request.ProjectId,
                    observation.ObservationId,
                    layer.LayerId,
                    request.ExpectedRevision,
                    observation.State,
                    processed.Quality,
                    processed.IsUsable ? observation.ErrorCode : SurveyErrorCode.PreprocessingFailed,
                    processed.IsUsable ? observation.ErrorMessage : processed.RejectionReason,
                    layer.AutomaticTransform,
                    null,
                    processed.StructureAsset,
                    processed.FeatureAsset,
                    processed.DisplayAsset,
                    processed.VisibleMaskAsset,
                    UsesCleanedDisplay: true),
                cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.PreprocessingFailed, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> AlignLayersAsync(
        SurveyLayerAlignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");
            if (_registrar is null)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "当前运行环境没有测绘配准器。");
            var selectedIds = request.LayerIds.Distinct().ToArray();
            if (selectedIds.Length < 2 || !selectedIds.Contains(request.AnchorLayerId))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "魔术贴至少需要两个图层，并且基准层必须位于选择中。");
            var selected = current.Layers.Where(item => selectedIds.Contains(item.LayerId)).ToArray();
            if (selected.Length != selectedIds.Length || selected.Any(item => item.IsDeleted))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "选择中包含不存在或已删除的图层。");
            var anchor = selected.Single(item => item.LayerId == request.AnchorLayerId);
            if (selected.Any(item => item.FloorId != anchor.FloorId))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "魔术贴只能对齐同一楼层的图层。");
            var observations = current.Observations.ToDictionary(item => item.ObservationId);
            var anchorObservation = observations[anchor.ObservationId];
            var anchorAsset = SelectDisplayAsset(anchor, anchorObservation);
            var items = new List<SurveyLayerOperationItem>();
            var mutations = new List<SurveyLayerMutation>();
            foreach (var layer in selected.Where(item => item.LayerId != anchor.LayerId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.IsLocked)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, "图层已锁定。"));
                    continue;
                }
                var observation = observations[layer.ObservationId];
                var match = await _registrar.RegisterAsync(
                    new SurveyRegistrationRequest(
                        observation,
                        anchorObservation,
                        anchor,
                        SelectDisplayAsset(layer, observation),
                        anchorAsset),
                    cancellationToken).ConfigureAwait(false);
                if (!match.IsAccepted)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, match.RejectionReason));
                    continue;
                }
                var transform = Compose(anchor.EffectiveTransform, match.RelativeTransform);
                mutations.Add(new SurveyLayerMutation(
                    layer.LayerId,
                    ManualTransformOverride: transform,
                    ReplaceManualTransform: true,
                    ObservationState: SurveyObservationState.Registered,
                    ObservationErrorCode: SurveyErrorCode.None,
                    ObservationErrorMessage: null,
                    ReplaceObservationStatus: true));
                items.Add(new SurveyLayerOperationItem(layer.LayerId, true, Transform: transform));
            }
            var snapshot = mutations.Count == 0
                ? current
                : await _projects.ApplyLayerBatchAsync(
                    new SurveyLayerBatchEditRequest(
                        request.CommandId,
                        request.ProjectId,
                        request.ExpectedRevision,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(
                new SurveyLayerOperationResult(snapshot, items));
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyLayerOperationResult>(SurveyErrorCode.RegistrationRejected, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> NormalizeLayerColorsAsync(
        SurveyLayerColorNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            // Reject queued/re-entrant commands before decoding and processing
            // every selected image.  Previously the conflict was discovered
            // only at the final database commit, after all native work had run.
            if (current.Project.Revision != request.ExpectedRevision)
            {
                throw new SurveyRevisionConflictException(
                    request.ProjectId,
                    request.ExpectedRevision,
                    current.Project.Revision);
            }
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");
            if (_rasterEditor is null)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "当前运行环境没有图层颜色处理器。");
            var ids = request.LayerIds.Distinct().ToArray();
            if (ids.Length < 2 || !ids.Contains(request.AnchorLayerId))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "融色至少需要两个图层，并且基准层必须位于选择中。");
            var selected = current.Layers.Where(layer => ids.Contains(layer.LayerId)).ToArray();
            if (selected.Length != ids.Length || selected.Any(layer => layer.IsDeleted))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "选择中包含不存在或已删除的图层。");
            var anchor = selected.Single(layer => layer.LayerId == request.AnchorLayerId);
            if (selected.Any(layer => layer.FloorId != anchor.FloorId))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "融色只能处理同一楼层的图层。");
            var observations = current.Observations.ToDictionary(item => item.ObservationId);
            var anchorObservation = observations[anchor.ObservationId];
            var items = new List<SurveyLayerOperationItem>();
            var mutations = new List<SurveyLayerMutation>();
            foreach (var layer in selected.Where(layer => layer.LayerId != anchor.LayerId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.IsLocked)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, "图层已锁定。"));
                    continue;
                }
                var observation = observations[layer.ObservationId];
                var normalized = await _rasterEditor.NormalizeColorsAsync(
                    request.ProjectId, layer, observation, anchor, anchorObservation, cancellationToken)
                    .ConfigureAwait(false);
                mutations.Add(new SurveyLayerMutation(
                    layer.LayerId,
                    ColorFilterAsset: normalized,
                    ReplaceColorFilter: true));
                items.Add(new SurveyLayerOperationItem(layer.LayerId, true, "颜色已匹配到基准层。"));
            }
            if (mutations.Count > 0)
                current = await _projects.ApplyLayerBatchAsync(new SurveyLayerBatchEditRequest(
                    request.CommandId, request.ProjectId, request.ExpectedRevision, mutations), cancellationToken)
                    .ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(new(current, items));
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyLayerOperationResult>(SurveyErrorCode.PreprocessingFailed, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> ApplyColorTemplateAsync(
        SurveyLayerColorTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.Revision != request.ExpectedRevision)
            {
                throw new SurveyRevisionConflictException(
                    request.ProjectId,
                    request.ExpectedRevision,
                    current.Project.Revision);
            }
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyLayerOperationResult>(
                    SurveyErrorCode.ProjectArchived,
                    "Archived survey projects are read-only.");
            if (_rasterEditor is null)
                return Failure<SurveyLayerOperationResult>(
                    SurveyErrorCode.InvalidState,
                    "The current environment has no layer color processor.");

            var entries = request.Entries
                .Where(entry => Enum.IsDefined(entry.Type))
                .Distinct()
                .ToArray();
            if (entries.Length == 0 || entries.Length > 256)
                return Failure<SurveyLayerOperationResult>(
                    SurveyErrorCode.InvalidState,
                    "A color template must contain between 1 and 256 color entries.");

            var requestedIds = request.LayerIds.Distinct().ToArray();
            if (requestedIds.Length == 0)
                return Failure<SurveyLayerOperationResult>(
                    SurveyErrorCode.InvalidState,
                    "At least one target layer is required.");

            var selected = current.Layers
                .Where(item => requestedIds.Contains(item.LayerId))
                .ToArray();
            if (selected.Length != requestedIds.Length || selected.Any(item => item.IsDeleted))
                return Failure<SurveyLayerOperationResult>(
                    SurveyErrorCode.InvalidState,
                    "One or more target layers do not exist or have been deleted.");

            var observations = current.Observations.ToDictionary(item => item.ObservationId);
            var items = new List<SurveyLayerOperationItem>(selected.Length);
            var mutations = new List<SurveyLayerMutation>(selected.Length);
            foreach (var layer in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var observation = observations[layer.ObservationId];
                    var filtered = await _rasterEditor.ApplyColorTemplateAsync(
                        request.ProjectId,
                        layer,
                        observation,
                        entries,
                        cancellationToken).ConfigureAwait(false);
                    mutations.Add(new SurveyLayerMutation(
                        layer.LayerId,
                        ColorFilterAsset: filtered,
                        ReplaceColorFilter: true));
                    items.Add(new SurveyLayerOperationItem(
                        layer.LayerId,
                        true,
                        "The color template was applied."));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    items.Add(new SurveyLayerOperationItem(
                        layer.LayerId,
                        false,
                        $"The color template could not be applied: {exception.Message}"));
                }
            }

            var snapshot = mutations.Count == 0
                ? current
                : await _projects.ApplyLayerBatchAsync(
                    new SurveyLayerBatchEditRequest(
                        request.CommandId,
                        request.ProjectId,
                        request.ExpectedRevision,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(
                new SurveyLayerOperationResult(
                    snapshot,
                    items));
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyLayerOperationResult>(SurveyErrorCode.PreprocessingFailed, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> ApplyMaskStrokeAsync(
        SurveyMaskStrokeRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rasterEditor is null)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "当前运行环境没有图层遮罩处理器。");
            if (request.Points.Count == 0 || request.Size is < 1d or > 1024d || !double.IsFinite(request.Size))
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "橡皮擦笔划无效。");
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");
            var requestedIds = request.LayerIds.Distinct().ToHashSet();
            var targets = current.Layers.Where(item => requestedIds.Contains(item.LayerId)).ToArray();
            var observations = current.Observations.ToDictionary(item => item.ObservationId);
            var items = new List<SurveyLayerOperationItem>();
            var mutations = new List<SurveyLayerMutation>();
            foreach (var layer in targets)
            {
                if (layer.FloorId != request.FloorId || layer.IsDeleted || !layer.IsVisible || layer.IsLocked)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, "图层不可见、已锁定或不属于当前楼层。"));
                    continue;
                }
                var mask = await _rasterEditor.ApplyHiddenMaskAsync(
                    request.ProjectId,
                    layer,
                    observations[layer.ObservationId],
                    request.Points,
                    request.Size,
                    request.Shape,
                    cancellationToken).ConfigureAwait(false);
                if (mask is null)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, "笔划没有与图层相交。"));
                    continue;
                }
                mutations.Add(new SurveyLayerMutation(
                    layer.LayerId,
                    HiddenMaskAsset: mask,
                    ReplaceHiddenMask: true));
                items.Add(new SurveyLayerOperationItem(layer.LayerId, true));
            }
            var snapshot = mutations.Count == 0
                ? current
                : await _projects.ApplyLayerBatchAsync(
                    new SurveyLayerBatchEditRequest(
                        request.CommandId,
                        request.ProjectId,
                        request.ExpectedRevision,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(
                new SurveyLayerOperationResult(snapshot, items));
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyLayerOperationResult>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> ApplyColorBrushAsync(
        SurveyColorBrushRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rasterEditor is null || request.Points.Count == 0 || request.Size is < 1d or > 1024d)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "画笔笔划无效。");
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            var layer = current.Layers.SingleOrDefault(item => item.LayerId == request.LayerId);
            if (layer is null || layer.IsDeleted || !layer.IsVisible || layer.IsLocked)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "主选图层不可编辑。");
            var observation = current.Observations.Single(item => item.ObservationId == layer.ObservationId);
            var asset = await _rasterEditor.ApplyColorBrushAsync(request.ProjectId, layer, observation,
                request.Points, request.Size, request.Shape, request.Color, cancellationToken).ConfigureAwait(false);
            if (asset is null)
                return SurveyOperationResult<SurveyLayerOperationResult>.Success(new SurveyLayerOperationResult(current,
                    [new SurveyLayerOperationItem(layer.LayerId, false, "笔划没有改变图层内容。" )]));
            var snapshot = await _projects.ApplyLayerBatchAsync(new SurveyLayerBatchEditRequest(request.CommandId,
                request.ProjectId, request.ExpectedRevision, [new SurveyLayerMutation(layer.LayerId,
                    ColorFilterAsset: asset, ReplaceColorFilter: true)]), cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(new SurveyLayerOperationResult(snapshot,
                [new SurveyLayerOperationItem(layer.LayerId, true)]));
        }
        catch (SurveyRevisionConflictException exception) { return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message); }
        catch (Exception exception) { return Fault<SurveyLayerOperationResult>(SurveyErrorCode.StorageUnavailable, exception); }
        finally { _gate.Release(); }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> ApplyColorFillAsync(
        SurveyColorFillRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rasterEditor is null || request.Tolerance > 255)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "颜料桶参数无效。");
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            var layer = current.Layers.SingleOrDefault(item => item.LayerId == request.LayerId);
            if (layer is null || layer.IsDeleted || !layer.IsVisible || layer.IsLocked)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "主选图层不可编辑。");
            var observation = current.Observations.Single(item => item.ObservationId == layer.ObservationId);
            var asset = await _rasterEditor.ApplyColorFillAsync(request.ProjectId, layer, observation,
                request.PixelX, request.PixelY, request.Tolerance, request.Color, cancellationToken).ConfigureAwait(false);
            if (asset is null)
                return SurveyOperationResult<SurveyLayerOperationResult>.Success(new SurveyLayerOperationResult(current,
                    [new SurveyLayerOperationItem(layer.LayerId, false, "填充区域与目标颜色相同或无有效区域。" )]));
            var snapshot = await _projects.ApplyLayerBatchAsync(new SurveyLayerBatchEditRequest(request.CommandId,
                request.ProjectId, request.ExpectedRevision, [new SurveyLayerMutation(layer.LayerId,
                    ColorFilterAsset: asset, ReplaceColorFilter: true)]), cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(new SurveyLayerOperationResult(snapshot,
                [new SurveyLayerOperationItem(layer.LayerId, true)]));
        }
        catch (SurveyRevisionConflictException exception) { return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message); }
        catch (Exception exception) { return Fault<SurveyLayerOperationResult>(SurveyErrorCode.StorageUnavailable, exception); }
        finally { _gate.Release(); }
    }

    public async Task<SurveyOperationResult<SurveyLayerOperationResult>> CorrectLayerVignetteAsync(
        SurveyLayerVignetteCorrectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rasterEditor is null)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "当前运行环境没有晕影校正处理器。");
            if (request.LayerIds.Count == 0
                || !double.IsFinite(request.CompensationStart)
                || request.CompensationStart is < 0d or > 1d
                || !double.IsFinite(request.CompensationStrength)
                || request.CompensationStrength is < 0d or > 1d)
            {
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.InvalidState, "晕影校正参数无效。");
            }

            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.Revision != request.ExpectedRevision)
            {
                throw new SurveyRevisionConflictException(
                    request.ProjectId,
                    request.ExpectedRevision,
                    current.Project.Revision);
            }
            if (current.Project.State == SurveyProjectState.Archived)
                return Failure<SurveyLayerOperationResult>(SurveyErrorCode.ProjectArchived, "已归档的测绘项目为只读。");

            var requestedIds = request.LayerIds.Distinct().ToArray();
            var requestedSet = requestedIds.ToHashSet();
            var layers = current.Layers.Where(item => requestedSet.Contains(item.LayerId)).ToArray();
            var observations = current.Observations.ToDictionary(item => item.ObservationId);
            var items = new List<SurveyLayerOperationItem>();
            var mutations = new List<SurveyLayerMutation>();
            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.IsDeleted || !layer.IsVisible || layer.IsLocked)
                {
                    items.Add(new SurveyLayerOperationItem(layer.LayerId, false, "图层不可见、已锁定或已删除。"));
                    continue;
                }
                var corrected = await _rasterEditor.CorrectVignetteAsync(
                    request.ProjectId,
                    layer,
                    observations[layer.ObservationId],
                    request.CompensationStart,
                    request.CompensationStrength,
                    cancellationToken).ConfigureAwait(false);
                mutations.Add(new SurveyLayerMutation(
                    layer.LayerId,
                    ColorFilterAsset: corrected,
                    ReplaceColorFilter: true));
                items.Add(new SurveyLayerOperationItem(layer.LayerId, true, "晕影校正已应用。"));
            }
            foreach (var missing in requestedIds.Where(id => layers.All(layer => layer.LayerId != id)))
                items.Add(new SurveyLayerOperationItem(missing, false, "图层不存在。"));

            if (mutations.Count > 0)
            {
                current = await _projects.ApplyLayerBatchAsync(
                    new SurveyLayerBatchEditRequest(
                        request.CommandId,
                        request.ProjectId,
                        request.ExpectedRevision,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
            }
            return SurveyOperationResult<SurveyLayerOperationResult>.Success(new(current, items));
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyLayerOperationResult>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyLayerOperationResult>(SurveyErrorCode.PreprocessingFailed, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Stream> OpenRenderedLayerAsync(
        Guid projectId,
        Guid layerId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await _projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new SurveyProjectNotFoundException(projectId);
        var layer = snapshot.Layers.Single(item => item.LayerId == layerId);
        var observation = snapshot.Observations.Single(item => item.ObservationId == layer.ObservationId);
        return await OpenRenderedLayerAsync(projectId, layer, observation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Stream> OpenRenderedLayerAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (layer.ProjectId != projectId
            || observation.ProjectId != projectId
            || layer.ObservationId != observation.ObservationId)
            throw new ArgumentException("Survey layer and observation identity do not match.");
        if (_rasterEditor is null)
            return await _assets.OpenReadAsync(projectId, SelectDisplayAsset(layer, observation), cancellationToken)
                .ConfigureAwait(false);
        var bytes = await _rasterEditor.RenderLayerAsync(
            projectId, layer, observation, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes.ToArray(), writable: false);
    }

    private static SurveyAssetReference SelectDisplayAsset(
        SurveyMapLayer layer,
        SurveyObservation observation) =>
        layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
}
