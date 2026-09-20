using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> DuplicateProjectAsync(
        Guid projectId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = await _projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(projectId);
            var duplicateId = Guid.NewGuid();
            var assets = await CopyAssetsAsync(source, duplicateId, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var project = source.Project with
            {
                ProjectId = duplicateId,
                Name = string.IsNullOrWhiteSpace(name)
                    ? $"{source.Project.Name} 副本"
                    : name.Trim(),
                State = source.Layers.Any(layer => !layer.IsDeleted)
                    ? SurveyProjectState.NeedsReview
                    : SurveyProjectState.Draft,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
                PublishedRevision = null
            };
            var duplicate = new SurveyProjectSnapshot(
                project,
                source.Floors,
                source.Observations.Select(observation => observation with
                {
                    ProjectId = duplicateId,
                    SourceAsset = assets[observation.SourceAsset.Sha256],
                    StructureAsset = RemapOptionalAsset(observation.StructureAsset, assets),
                    FeatureAsset = RemapOptionalAsset(observation.FeatureAsset, assets),
                    DisplayAsset = RemapOptionalAsset(observation.DisplayAsset, assets),
                    VisibleMaskAsset = RemapOptionalAsset(observation.VisibleMaskAsset, assets)
                }).ToArray(),
                source.Layers.Select(layer => layer with
                {
                    ProjectId = duplicateId,
                    HiddenMaskAsset = RemapOptionalAsset(layer.HiddenMaskAsset, assets),
                    ColorFilterAsset = RemapOptionalAsset(layer.ColorFilterAsset, assets)
                }).ToArray(),
                source.Constraints.Select(constraint => constraint with
                {
                    ProjectId = duplicateId
                }).ToArray());
            var imported = await _projects.ImportSnapshotAsync(
                duplicate,
                Guid.NewGuid(),
                cancellationToken).ConfigureAwait(false);
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(imported);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<SurveyProjectSnapshot>(
                SurveyErrorCode.Cancelled,
                "复制测绘项目已取消。");
        }
        catch (SurveyProjectNotFoundException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.ProjectNotFound, exception.Message);
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

    private async Task<IReadOnlyDictionary<string, SurveyAssetReference>> CopyAssetsAsync(
        SurveyProjectSnapshot source,
        Guid duplicateId,
        CancellationToken cancellationToken)
    {
        var references = source.Observations
            .SelectMany(observation => new[]
            {
                observation.SourceAsset,
                observation.StructureAsset,
                observation.FeatureAsset,
                observation.DisplayAsset,
                observation.VisibleMaskAsset
            })
            .Where(asset => asset is not null)
            .Cast<SurveyAssetReference>()
            .Concat(source.Layers
                .Select(layer => layer.HiddenMaskAsset)
                .Concat(source.Layers.Select(layer => layer.ColorFilterAsset))
                .Where(asset => asset is not null)
                .Cast<SurveyAssetReference>())
            .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var copied = new Dictionary<string, SurveyAssetReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in references)
        {
            await using var stream = await _assets.OpenReadAsync(
                source.Project.ProjectId,
                asset,
                cancellationToken).ConfigureAwait(false);
            var clone = await _assets.PutStreamAsync(
                duplicateId,
                stream,
                Path.GetExtension(asset.RelativePath),
                asset.MediaType,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Sha256,
                cancellationToken).ConfigureAwait(false);
            copied.Add(asset.Sha256, clone);
        }
        return copied;
    }

    private static SurveyAssetReference? RemapOptionalAsset(
        SurveyAssetReference? asset,
        IReadOnlyDictionary<string, SurveyAssetReference> assets) =>
        asset is null ? null : assets[asset.Sha256];
}
