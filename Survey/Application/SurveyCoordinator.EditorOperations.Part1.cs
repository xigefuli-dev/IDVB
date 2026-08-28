using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;
public sealed partial class SurveyCoordinator
{

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
