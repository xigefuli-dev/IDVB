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
        if (profile is not null
            && !string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
        {
            return GetSafeMapFilePath(
                GetMapDirectory(record.Id),
                profile.SideEntranceFeatureFileName);
        }

        return Path.Combine(
            GetMapDirectory(record.Id),
            GetSideEntranceFeatureFileName(floorKey));
    }

    /// <summary>
    /// 若侧门锚点已标注，为该楼层生成侧门特征图并更新 profile 的相关字段。
    /// 若锚点未标注或识别图不存在，则静默跳过。
    /// </summary>
    private async Task TryGenerateSideEntranceFeatureAsync(
        string stagingDirectory,
        FloorRecognitionProfile profile,
        int featureRadius)
    {
        var sideAnchor = profile.FindAnchor("side-entrance");
        if (sideAnchor?.IsMarked is not true)
            return;

        // 找到已在 staging 中写好的识别图路径
        var recognitionFileName = GetFloorRecognitionFileName(profile.FloorKey);
        var recognitionPath = Path.Combine(stagingDirectory, recognitionFileName);
        if (!File.Exists(recognitionPath))
            return;

        try
        {
            using var recognitionMat = Cv2.ImRead(recognitionPath, ImreadModes.Grayscale);
            if (recognitionMat.Empty())
                return;

            using var result = _sideEntrancePreprocessor.Process(
                recognitionMat, sideAnchor.Bounds!, featureRadius);

            var featureFileName = GetSideEntranceFeatureFileName(profile.FloorKey);
            var featurePath = Path.Combine(stagingDirectory, featureFileName);
            if (!Cv2.ImWrite(featurePath, result.Feature))
                return;

            // 计算特征图和源识别图的 SHA-256
            await using var featureStream = File.OpenRead(featurePath);
            var featureHash = await SHA256.HashDataAsync(featureStream);
            await using var sourceStream = File.OpenRead(recognitionPath);
            var sourceHash = await SHA256.HashDataAsync(sourceStream);

            profile.SideEntranceFeatureFileName = featureFileName;
            profile.SideEntranceFeatureSha256 =
                Convert.ToHexString(featureHash).ToLowerInvariant();
            profile.SideEntranceFeatureSourceSha256 =
                Convert.ToHexString(sourceHash).ToLowerInvariant();
            profile.SideEntranceFeatureCenterX = result.CenterX;
            profile.SideEntranceFeatureCenterY = result.CenterY;
            profile.SideEntranceFeatureRadius   = result.Radius;
        }
        catch
        {
            // 特征生成失败不阻断主保存流程；下次打开编辑页面或批量重建时会重试
        }
    }

    /// <summary>
    /// 批量为所有地图重新生成侧门特征图（半径参数改变时调用）。
    /// 每张地图处理完毕后通知进度；出错地图跳过。
    /// </summary>
    public async Task RebuildAllSideEntranceFeaturesAsync(
        int featureRadius,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        featureRadius = Math.Clamp(featureRadius, 20, 500);
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
                var sideAnchor = profile.FindAnchor("side-entrance");
                if (sideAnchor?.IsMarked is not true)
                    continue;

                var recognitionPath = GetFloorRecognitionPath(record, floorDef.Key);
                if (!File.Exists(recognitionPath))
                    continue;

                try
                {
                    using var recognitionMat = Cv2.ImRead(recognitionPath, ImreadModes.Grayscale);
                    if (recognitionMat.Empty())
                        continue;

                    using var result = _sideEntrancePreprocessor.Process(
                        recognitionMat, sideAnchor.Bounds!, featureRadius);

                    var featureFileName = GetSideEntranceFeatureFileName(floorDef.Key);
                    var featurePath = Path.Combine(mapDirectory, featureFileName);
                    if (!Cv2.ImWrite(featurePath, result.Feature))
                        continue;

                    await using var featureStream = File.OpenRead(featurePath);
                    var featureHash = await SHA256.HashDataAsync(featureStream, cancellationToken);
                    await using var sourceStream = File.OpenRead(recognitionPath);
                    var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);

                    profile.SideEntranceFeatureFileName = featureFileName;
                    profile.SideEntranceFeatureSha256 =
                        Convert.ToHexString(featureHash).ToLowerInvariant();
                    profile.SideEntranceFeatureSourceSha256 =
                        Convert.ToHexString(sourceHash).ToLowerInvariant();
                    profile.SideEntranceFeatureCenterX = result.CenterX;
                    profile.SideEntranceFeatureCenterY = result.CenterY;
                    profile.SideEntranceFeatureRadius   = result.Radius;

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
}
