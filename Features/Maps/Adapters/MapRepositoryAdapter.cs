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

    public async Task<object> SaveAsync(object draft) =>
        await _repo.SaveAsync((MapDraft)draft);

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

    public async Task<object> ToggleVariantGroupAsync(
        string className,
        IReadOnlyCollection<Guid> selectedMapIds) =>
        await _repo.ToggleVariantGroupAsync(className, selectedMapIds);

    public Task ReorderClassAsync(string className) =>
        _repo.ReorderClassAsync(className);

    public Task RenameClassAsync(string oldName, string newName) =>
        _repo.RenameClassAsync(oldName, newName);

    public Task<IReadOnlyList<Guid>> SetClassBackgroundRemovalAsync(
        string className,
        bool enabled,
        int intensity,
        CancellationToken cancellationToken = default) =>
        _repo.SetClassBackgroundRemovalAsync(className, enabled, intensity, cancellationToken);

    public Task SetClassScanFloorAsync(
        string className,
        string? floorKey,
        CancellationToken cancellationToken = default) =>
        _repo.SetClassScanFloorAsync(className, floorKey, cancellationToken);

    public object GetCatalogRevision() => _repo.GetCatalogRevision();

    public Task RepairImageMetadataAsync(CancellationToken cancellationToken = default) =>
        _repo.RepairImageMetadataAsync(cancellationToken);

    public async Task EnsureDerivedAssetsAsync(IReadOnlyList<object> maps) =>
        await _repo.EnsureDerivedAssetsAsync(maps.Cast<MapRecord>().ToList());

    public Task RebuildAllSideEntranceFeaturesAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default) =>
        _repo.RebuildAllSideEntranceFeaturesAsync(progress, cancellationToken);

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
/*
 * 文件职责：MapRepositoryAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
