// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 地图目录持久化抽象。管理 maps.json 中的地图记录及其图片资产。
/// </summary>
public interface IMapRepository
{
    /// <summary>
    /// 获取所有已注册地图（按序号排序）。
    /// </summary>
    Task<IReadOnlyList</* MapRecord */ object>> GetMapsAsync();

    /// <summary>
    /// 创建地图编辑草稿（含所有楼层图片路径、配置等）。
    /// </summary>
    Task<object? /* MapDraft? */> CreateDraftAsync(Guid id);

    /// <summary>
    /// 保存地图草稿（新建或更新）。
    /// </summary>
    Task</* MapRecord */ object> SaveAsync(
        object /* MapDraft */ draft,
        int sideEntranceFeatureRadius = 0);

    /// <summary>
    /// 删除指定地图及其所有资产。
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// 获取地图目录快照（含 Class 列表和所有地图记录）。
    /// </summary>
    Task</* MapCatalogSnapshot */ object> GetCatalogSnapshotAsync();

    /// <summary>
    /// 验证指定地图的楼层图片绑定完整性。
    /// </summary>
    Task VerifyMapContentAsync(Guid id);

    /// <summary>
    /// 创建新的地图 Class。
    /// </summary>
    Task<string> CreateClassAsync(string name);

    /// <summary>
    /// 批量导入 IDVM 包中的 Class 和地图。
    /// </summary>
    Task</* MapImportBatchResult */ object> ImportBatchAsync(
        IReadOnlyList</* MapImportClassDraft */ object> sourceClasses,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定 Class 及其下所有地图。
    /// </summary>
    Task</* MapClassDeletionResult */ object> DeleteClassAsync(string className);

    /// <summary>
    /// 一次性批量将所有地图重命名为默认序号名称。
    /// </summary>
    Task BatchRenameAllMapsToDefaultNamesAsync();

    /// <summary>
    /// 重命名单个 Class。
    /// </summary>
    Task RenameClassAsync(string oldName, string newName);

    /// <summary>
    /// 获取目录文件修订标识（用于检测外部变更）。
    /// </summary>
    /* MapCatalogRevision */ object GetCatalogRevision();

    /// <summary>
    /// 修复所有地图的图片元数据（SHA-256、尺寸等）并更新缩略图。
    /// </summary>
    Task RepairImageMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 确保所有指定地图的派生资产（识别图、叠加图）存在且正确。
    /// </summary>
    Task EnsureDerivedAssetsAsync(IReadOnlyList</* MapRecord */ object> maps);

    /// <summary>
    /// 批量为所有地图重新生成侧门特征图（半径参数改变时调用）。
    /// </summary>
    Task RebuildAllSideEntranceFeaturesAsync(
        int featureRadius,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default);

    // ── 楼层图片路径解析 ──

    /// <summary>
    /// 获取一楼原始图片路径。
    /// </summary>
    string GetFloorOnePath(object /* MapRecord */ record);

    /// <summary>
    /// 获取二楼原始图片路径。
    /// </summary>
    string GetFloorTwoPath(object /* MapRecord */ record);

    /// <summary>
    /// 获取指定楼层原始图片路径。
    /// </summary>
    string GetFloorImagePath(object /* MapRecord */ record, string floorKey);

    /// <summary>
    /// 获取指定楼层的识别图路径。
    /// </summary>
    string GetFloorRecognitionPath(object /* MapRecord */ record, string floorKey);

    /// <summary>
    /// 获取指定楼层的叠加图路径。
    /// </summary>
    string GetFloorOverlayPath(object /* MapRecord */ record, string floorKey);

    /// <summary>
    /// 获取指定楼层的缩略图路径。
    /// </summary>
    string GetFloorThumbnailPath(object /* MapRecord */ record, string floorKey);

    /// <summary>
    /// 获取指定楼层的侧门特征图路径。
    /// </summary>
    string GetSideEntranceFeaturePath(object /* MapRecord */ record, string floorKey);
}
