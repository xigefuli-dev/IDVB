using OpenCvSharp;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // ── 侧门特征 ──────────────────────────────────────────────────────

    /// <summary>获取侧门特征图文件名（相对 map 目录）。</summary>
    private static string GetSideEntranceFeatureFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1-side-entrance-feature.png",
            "2f" => "floor-2-side-entrance-feature.png",
            _ => $"floor-{floorKey}-side-entrance-feature.png"
        };

    /// <summary>获取侧门特征图的完整磁盘路径。</summary>
    public string GetSideEntranceFeaturePath(MapRecord record, string floorKey)
    {
        var profile = record.Recognition.GetFloor(floorKey);
        return profile is not null && !string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName)
            ? GetSafeMapFilePath(GetMapDirectory(record.Id), profile.SideEntranceFeatureFileName)
            : Path.Combine(GetMapDirectory(record.Id), GetSideEntranceFeatureFileName(floorKey));
    }

    /// <summary>
    /// 解析指定地图楼层侧门特征的生成源图像路径。
    /// 严格只使用预制二值轮廓图（prebuilt-{floorKey}.png），彻底删除从攻略地图裁剪灰度图的旧路径。
    /// </summary>
    private (string path, string sourceKind) ResolveSideEntranceFeatureSource(MapRecord record, string floorKey)
    {
        var localPrebuilt = Path.Combine(GetMapDirectory(record.Id), $"prebuilt-{floorKey}.png");
        if (File.Exists(localPrebuilt)) return (localPrebuilt, "PrebuiltStructureLine");
        if (HasPrebuiltStructureLine(record, floorKey))
        {
            var prebuiltPath = GetPrebuiltStructureLinePath(record, floorKey);
            if (File.Exists(prebuiltPath)) return (prebuiltPath, "PrebuiltStructureLine");
        }
        throw new InvalidOperationException($"地图 {record.DisplayName} 楼层 {floorKey} 缺少预制二值轮廓图 prebuilt-{floorKey}.png。");
    }

    /// <summary>
    /// 在暂存目录（staging）中解析楼层侧门特征的生成源图像路径。
    /// 严格只使用预制二值轮廓图。
    /// </summary>
    private static (string path, string sourceKind) ResolveSideEntranceStagingFeatureSource(
        string stagingDirectory, string floorKey)
    {
        var prebuiltPath = Path.Combine(stagingDirectory, $"prebuilt-{floorKey}.png");
        if (File.Exists(prebuiltPath))
            return (prebuiltPath, "PrebuiltStructureLine");
        throw new InvalidOperationException($"暂存目录缺少预制二值轮廓图 prebuilt-{floorKey}.png。");
    }

    /// <summary>
    /// 若扫描门锚点已标注，为该楼层生成门特征图并更新 profile 的相关字段。
    /// 若锚点未标注或源图像不存在，则静默跳过。
    /// </summary>
    private async Task TryGenerateSideEntranceFeatureAsync(
        string stagingDirectory,
        FloorRecognitionProfile profile)
    {
        if (IsCvDisabledForSafeMode())
            return;
        var scanAnchor = GetScanFeatureAnchor(profile);
        if (scanAnchor?.IsMarked is not true)
            return;

        string sourcePath;
        string sourceKind;
        try
        {
            (sourcePath, sourceKind) =
                ResolveSideEntranceStagingFeatureSource(stagingDirectory, profile.FloorKey);
        }
        catch
        {
            return;
        }

        if (!File.Exists(sourcePath))
            return;

        try
        {
            using var sourceMat = Cv2.ImRead(sourcePath, ImreadModes.Grayscale);
            if (sourceMat.Empty())
                return;

            using var result = _sideEntrancePreprocessor.Value.Process(
                sourceMat,
                scanAnchor.Bounds!,
                SideEntranceScanRules.FeatureRegionRatio,
                SideEntranceScanRules.ClampFeatureToBounds);

            var featureFileName = GetSideEntranceFeatureFileName(profile.FloorKey);
            var featurePath = Path.Combine(stagingDirectory, featureFileName);
            if (!Cv2.ImWrite(featurePath, result.Feature))
                return;

            // 计算特征图和源图像的 SHA-256
            await using var featureStream = File.OpenRead(featurePath);
            var featureHash = await SHA256.HashDataAsync(featureStream);
            await using var sourceStream = File.OpenRead(sourcePath);
            var sourceHash = await SHA256.HashDataAsync(sourceStream);

            var featureSha = Convert.ToHexString(featureHash).ToLowerInvariant();
            var sourceSha = Convert.ToHexString(sourceHash).ToLowerInvariant();

            profile.SideEntranceFeatureFileName = featureFileName;
            profile.SideEntranceFeatureSha256 = featureSha;
            profile.SideEntranceFeatureSourceSha256 = sourceSha;
            profile.SideEntranceFeatureAlgorithmVersion =
                SideEntranceFeaturePreprocessor.AlgorithmVersion;
            profile.SideEntranceFeatureCenterX = result.CenterX;
            profile.SideEntranceFeatureCenterY = result.CenterY;
            profile.SideEntranceFeatureRadius   = result.Radius;

            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"侧门特征已生成 · floorKey={profile.FloorKey} · source={sourceKind} · size={result.Feature.Width}x{result.Feature.Height} · ratio={SideEntranceScanRules.FeatureRegionRatio:F2} · sha={featureSha[..8]}",
                details: BuildSideFeatureLogDetails(
                    null, profile.FloorKey, sourceKind, sourcePath, sourceSha, result));
        }
        catch
        {
            // 特征生成失败不阻断主保存流程；下次打开编辑页面或批量重建时会重试
        }
    }

    /// <summary>
    /// 批量按当前侧门 TOML 配置重新生成所有地图的侧门特征图。
    /// 每张地图处理完毕后通知进度；出错地图跳过。
    /// </summary>
    public async Task RebuildAllSideEntranceFeaturesAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        MapCatalogDocument catalog;
        try
        {
            catalog = await ReadCatalogAsync();
        }
        finally
        {
            Gate.Release();
        }

        var maps = catalog.Maps;
        var total = maps.Count;
        var done  = 0;

        foreach (var record in maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            var mapDirectory = GetMapDirectory(record.Id);

            foreach (var floorDef in MapFloorRules.GetOrderedFloors(record))
            {
                var profile = MapFloorRules.GetFloorProfile(record, floorDef.Key);
                if (profile is null)
                    continue;
                var scanAnchor = MapScanFloorRules.GetScanFeatureAnchor(
                    record,
                    floorDef.Key);
                if (scanAnchor?.IsMarked is not true)
                    continue;

                string sourcePath;
                string sourceKind;
                try
                {
                    (sourcePath, sourceKind) = ResolveSideEntranceFeatureSource(record, floorDef.Key);
                }
                catch
                {
                    continue;
                }

                if (!File.Exists(sourcePath))
                    continue;

                try
                {
                    using var sourceMat = Cv2.ImRead(sourcePath, ImreadModes.Grayscale);
                    if (sourceMat.Empty())
                        continue;

                    using var result = _sideEntrancePreprocessor.Value.Process(
                        sourceMat,
                        scanAnchor.Bounds!,
                        SideEntranceScanRules.FeatureRegionRatio,
                        SideEntranceScanRules.ClampFeatureToBounds);

                    var featureFileName = GetSideEntranceFeatureFileName(floorDef.Key);
                    var featurePath = Path.Combine(mapDirectory, featureFileName);
                    if (!Cv2.ImWrite(featurePath, result.Feature))
                        continue;

                    await using var featureStream = File.OpenRead(featurePath);
                    var featureHash = await SHA256.HashDataAsync(featureStream, cancellationToken);
                    await using var sourceStream = File.OpenRead(sourcePath);
                    var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);

                    var featureSha = Convert.ToHexString(featureHash).ToLowerInvariant();
                    var sourceSha = Convert.ToHexString(sourceHash).ToLowerInvariant();

                    profile.SideEntranceFeatureFileName = featureFileName;
                    profile.SideEntranceFeatureSha256 = featureSha;
                    profile.SideEntranceFeatureSourceSha256 = sourceSha;
                    profile.SideEntranceFeatureAlgorithmVersion =
                        SideEntranceFeaturePreprocessor.AlgorithmVersion;
                    profile.SideEntranceFeatureCenterX = result.CenterX;
                    profile.SideEntranceFeatureCenterY = result.CenterY;
                    profile.SideEntranceFeatureRadius   = result.Radius;

                    MapLogCollector.Instance.Append(
                        MapLogCategory.GateDetection,
                        MapLogLevel.Info,
                        $"侧门特征已重建 · map={record.DisplayName}#{floorDef.Key} · source={sourceKind} · size={result.Feature.Width}x{result.Feature.Height} · ratio={SideEntranceScanRules.FeatureRegionRatio:F2} · sha={featureSha[..8]}",
                        details: BuildSideFeatureLogDetails(
                            record.Id, floorDef.Key, sourceKind, sourcePath, sourceSha, result));

                    // 同步到 Floors 字典
                    record.Recognition.Floors[floorDef.Key] = profile;
                    changed = true;
                }
                catch
                {
                    // 单张地图出错跳过，不影响其他地图
                }
            }

            if (changed)
            {
                await Gate.WaitAsync(cancellationToken);
                try
                {
                    var liveCatalog = await ReadCatalogAsync();
                    var stored = liveCatalog.Maps.FirstOrDefault(m => m.Id == record.Id);
                    if (stored is not null)
                    {
                        foreach (var floorDef in MapFloorRules.GetOrderedFloors(record))
                        {
                            var srcProfile = MapFloorRules.GetFloorProfile(record, floorDef.Key);
                            var dstProfile = MapFloorRules.GetFloorProfile(stored, floorDef.Key);
                            if (srcProfile is null || dstProfile is null)
                                continue;
                            dstProfile.SideEntranceFeatureFileName =
                                srcProfile.SideEntranceFeatureFileName;
                            dstProfile.SideEntranceFeatureSha256 =
                                srcProfile.SideEntranceFeatureSha256;
                            dstProfile.SideEntranceFeatureSourceSha256 =
                                srcProfile.SideEntranceFeatureSourceSha256;
                            dstProfile.SideEntranceFeatureAlgorithmVersion =
                                srcProfile.SideEntranceFeatureAlgorithmVersion;
                            dstProfile.SideEntranceFeatureCenterX =
                                srcProfile.SideEntranceFeatureCenterX;
                            dstProfile.SideEntranceFeatureCenterY =
                                srcProfile.SideEntranceFeatureCenterY;
                            dstProfile.SideEntranceFeatureRadius =
                                srcProfile.SideEntranceFeatureRadius;
                            stored.Recognition.Floors[floorDef.Key] = dstProfile;
                        }
                        stored.Recognition.EnsureStandardAnchors();
                    }
                    await WriteCatalogAsync(liveCatalog);
                }
                finally
                {
                    Gate.Release();
                }
            }

            progress?.Report((++done, total));
        }
    }

    /// <summary>
    /// IDVM 导入场景：将包内已预计算的侧门特征图复制到 staging，并写入 SHA-256 元数据。
    /// </summary>
    private static async Task CopySideEntranceFeatureAsync(
        string importedFeaturePath,
        string stagingDirectory,
        FloorRecognitionProfile profile)
    {
        try
        {
            var featureFileName = GetSideEntranceFeatureFileName(profile.FloorKey);
            var featureTarget = Path.Combine(stagingDirectory, featureFileName);
            await using (var src = File.OpenRead(importedFeaturePath))
            await using (var dst = File.Create(featureTarget))
                await src.CopyToAsync(dst);

            await using var hashStream = File.OpenRead(featureTarget);
            var hash = await SHA256.HashDataAsync(hashStream);
            profile.SideEntranceFeatureFileName = featureFileName;
            profile.SideEntranceFeatureSha256   =
                Convert.ToHexString(hash).ToLowerInvariant();
            // SideEntranceFeatureCenterX/Y/Radius 已由 IDVM 导入者填写
        }
        catch
        {
            // 复制失败不阻断主保存流程
        }
    }

    /// <summary>
    /// Validates every dependency of a persisted side-entrance feature. A file
    /// being readable is insufficient: it must belong to the current source
    /// recognition image and the current preprocessing algorithm.
    /// </summary>
    internal bool TryGetValidSideEntranceFeaturePath(
        MapRecord record,
        string floorKey,
        out string path,
        out string failureReason)
    {
        path = string.Empty;
        failureReason = string.Empty;
        var profile = MapFloorRules.GetFloorProfile(record, floorKey);
        if (profile is null
            || string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
        {
            failureReason = "侧门特征尚未生成。";
            return false;
        }

        if (!string.Equals(
                profile.SideEntranceFeatureAlgorithmVersion,
                SideEntranceFeaturePreprocessor.AlgorithmVersion,
                StringComparison.Ordinal))
        {
            failureReason = "侧门特征算法版本已过期。";
            return false;
        }

        path = GetSideEntranceFeaturePath(record, floorKey);
        string sourcePath;
        try
        {
            (sourcePath, _) = ResolveSideEntranceFeatureSource(record, floorKey);
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }

        if (!File.Exists(path) || !File.Exists(sourcePath))
        {
            failureReason = "侧门特征或其源图像不存在。";
            return false;
        }

        try
        {
            var featureHash = ComputeFileSha256(path);
            var sourceHash = ComputeFileSha256(sourcePath);
            if (!string.Equals(featureHash, profile.SideEntranceFeatureSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sourceHash, profile.SideEntranceFeatureSourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "侧门特征哈希或源图像哈希不匹配。";
                return false;
            }
        }
        catch (Exception exception)
        {
            failureReason = $"侧门特征完整性校验失败：{exception.Message}";
            return false;
        }

        return true;
    }

    /// <summary>Regenerates one missing or stale side-entrance feature in place.</summary>
    private bool EnsureCurrentSideEntranceFeature(
        MapRecord record,
        string floorKey,
        FloorRecognitionProfile profile,
        string? recognitionPath = null)
    {
        if (IsCvDisabledForSafeMode())
            return false;
        if (MapScanFloorRules.GetScanFeatureAnchor(record, floorKey)
                is not { IsMarked: true } anchor
            || anchor.Bounds?.IsValid is not true)
        {
            return false;
        }

        string sourcePath;
        string sourceKind;
        try
        {
            (sourcePath, sourceKind) = ResolveSideEntranceFeatureSource(record, floorKey);
        }
        catch
        {
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            return false;
        }

        if (TryGetValidSideEntranceFeaturePath(record, floorKey, out _, out _))
            return false;

        try
        {
            using var sourceMat = Cv2.ImRead(sourcePath, ImreadModes.Grayscale);
            if (sourceMat.Empty())
                return false;
            using var result = _sideEntrancePreprocessor.Value.Process(
                sourceMat,
                anchor.Bounds,
                SideEntranceScanRules.FeatureRegionRatio,
                SideEntranceScanRules.ClampFeatureToBounds);
            var fileName = GetSideEntranceFeatureFileName(floorKey);
            var featurePath = Path.Combine(GetMapDirectory(record.Id), fileName);
            if (!Cv2.ImWrite(featurePath, result.Feature))
                return false;

            var featureSha = ComputeFileSha256(featurePath);
            var sourceSha = ComputeFileSha256(sourcePath);

            profile.SideEntranceFeatureFileName = fileName;
            profile.SideEntranceFeatureSha256 = featureSha;
            profile.SideEntranceFeatureSourceSha256 = sourceSha;
            profile.SideEntranceFeatureAlgorithmVersion =
                SideEntranceFeaturePreprocessor.AlgorithmVersion;
            profile.SideEntranceFeatureCenterX = result.CenterX;
            profile.SideEntranceFeatureCenterY = result.CenterY;
            profile.SideEntranceFeatureRadius = result.Radius;

            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"侧门特征已更新 · map={record.DisplayName}#{floorKey} · source={sourceKind} · size={result.Feature.Width}x{result.Feature.Height} · ratio={SideEntranceScanRules.FeatureRegionRatio:F2} · sha={featureSha[..8]}",
                details: BuildSideFeatureLogDetails(
                    record.Id, floorKey, sourceKind, sourcePath, sourceSha, result));

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> BuildSideFeatureLogDetails(
        Guid? mapId,
        string floorKey,
        string sourceKind,
        string sourcePath,
        string sourceSha,
        SideEntranceFeatureResult result) => new()
    {
        ["mapId"] = mapId,
        ["floorKey"] = floorKey,
        ["sourceKind"] = sourceKind,
        ["sourcePath"] = sourcePath,
        ["sourceSha256"] = sourceSha,
        ["featureWidth"] = result.Feature.Width,
        ["featureHeight"] = result.Feature.Height,
        ["featureCenterX"] = result.CenterX,
        ["featureCenterY"] = result.CenterY,
        ["algorithmVersion"] = SideEntranceFeaturePreprocessor.AlgorithmVersion
    };

    private static bool IsCvDisabledForSafeMode() =>
        string.Equals(Environment.GetEnvironmentVariable("IDVB_SAFE_MODE"), "1", StringComparison.Ordinal);

    private static RecognitionAnchor? GetScanFeatureAnchor(FloorRecognitionProfile profile) =>
        profile.Floor == MapFloor.First
            ? profile.FindAnchor("side-entrance")
            : profile.FindAnchor(MapScanFloorRules.SecondaryGateAnchorKey);

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
/*
 * 文件职责：MapRepository.SideEntrance。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
