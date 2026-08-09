using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IMapRepository 适配器 — 委托给 MapRepository。</summary>
public sealed class MapRepositoryAdapter : IMapRepository
{
    private readonly MapRepository _repo = new();

    public async Task<IReadOnlyList<object>> GetMapsAsync()
    {
        var maps = await _repo.GetMapsAsync();
        return maps!;
    }

    public async Task<object?> CreateDraftAsync(Guid id) =>
        await _repo.CreateDraftAsync(id);

    public async Task<object> SaveAsync(object draft, int sideEntranceFeatureRadius = 0) =>
        await _repo.SaveAsync((MapDraft)draft, sideEntranceFeatureRadius);

    public Task DeleteAsync(Guid id) =>
        _repo.DeleteAsync(id);

    public async Task<object> GetCatalogSnapshotAsync() =>
        await _repo.GetCatalogSnapshotAsync();

    public Task VerifyMapContentAsync(Guid id) =>
        _repo.VerifyMapContentAsync(id);

    public Task<string> CreateClassAsync(string name) =>
        _repo.CreateClassAsync(name);

    public async Task<object> ImportBatchAsync(IReadOnlyList<object> sourceClasses,
        CancellationToken cancellationToken = default) =>
        await _repo.ImportBatchAsync(
            sourceClasses.Cast<MapImportClassDraft>().ToList(),
            cancellationToken);

    public async Task<object> DeleteClassAsync(string className) =>
        await _repo.DeleteClassAsync(className);

    public Task BatchRenameAllMapsToDefaultNamesAsync() =>
        _repo.BatchRenameAllMapsToDefaultNamesAsync();

    public Task RenameClassAsync(string oldName, string newName) =>
        _repo.RenameClassAsync(oldName, newName);

    public object GetCatalogRevision() => _repo.GetCatalogRevision();

    public Task RepairImageMetadataAsync(CancellationToken cancellationToken = default) =>
        _repo.RepairImageMetadataAsync(cancellationToken);

    public async Task EnsureDerivedAssetsAsync(IReadOnlyList<object> maps) =>
        await _repo.EnsureDerivedAssetsAsync(maps.Cast<MapRecord>().ToList());

    public Task RebuildAllSideEntranceFeaturesAsync(int featureRadius,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default) =>
        _repo.RebuildAllSideEntranceFeaturesAsync(featureRadius, progress, cancellationToken);

    public string GetFloorOnePath(object record) =>
        _repo.GetFloorOnePath((MapRecord)record);

    public string GetFloorTwoPath(object record) =>
        _repo.GetFloorTwoPath((MapRecord)record);

    public string GetFloorImagePath(object record, string floorKey) =>
        _repo.GetFloorImagePath((MapRecord)record, floorKey);

    public string GetFloorRecognitionPath(object record, string floorKey) =>
        _repo.GetFloorRecognitionPath((MapRecord)record, floorKey);

    public string GetFloorOverlayPath(object record, string floorKey) =>
        _repo.GetFloorOverlayPath((MapRecord)record, floorKey);

    public string GetFloorThumbnailPath(object record, string floorKey) =>
        _repo.GetFloorThumbnailPath((MapRecord)record, floorKey);

    public string GetSideEntranceFeaturePath(object record, string floorKey) =>
        _repo.GetSideEntranceFeaturePath((MapRecord)record, floorKey);
}
