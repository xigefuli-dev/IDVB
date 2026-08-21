// IDVB Remaster — 后台扫描（Background Scan）纯逻辑规则
// 与 SessionOrchestrator 解耦：可被测试项目独立链接，不依赖 WinUI 主应用。

using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 后台扫描状态。开启「后台扫描」后，快捷扫描只静默识别地图并标记完成；
/// 玩家第一次打开游戏地图时才按顺序进入候选/缩放并尝试一次对齐。
/// </summary>
public enum BackgroundScanStatus
{
    /// <summary>无进行中或待消费的后台扫描。</summary>
    Idle = 0,
    // 值 1 曾用于 BackgroundScanStatus.Running，已移除（状态由编排器同步持有，
    // 不存在需要标记「进行中」的异步窗口），保留占位避免枚举整数值漂移。
    /// <summary>后台识别成功，已确定地图身份（无变换，等待开图对齐）。</summary>
    CompletedIdentified = 2,
    /// <summary>后台识别有歧义，候选列表待玩家开图确认。</summary>
    CompletedAmbiguous = 3,
    /// <summary>后台识别失败，开图时提示重新扫描。</summary>
    CompletedFailed = 4,
}

/// <summary>后台扫描完成类型的判定产物。</summary>
internal sealed record BackgroundScanOutcome(
    BackgroundScanStatus Status,
    RuntimeMapRecognition? Identity,
    IReadOnlyList<MapRecognitionChoice>? Choices,
    string? FailureReason);

internal static class BackgroundScanRules
{
    /// <summary>
    /// 根据后台识别状态判定完成类型。纯函数，供单测驱动。
    /// </summary>
    public static BackgroundScanOutcome ClassifyBackgroundScan(
        RuntimeMapRecognition? identity,
        IReadOnlyList<MapRecognitionChoice>? choices,
        string? failureReason)
    {
        if (identity is not null)
        {
            return new BackgroundScanOutcome(
                BackgroundScanStatus.CompletedIdentified,
                identity,
                null,
                null);
        }

        if (choices is { Count: > 0 })
        {
            return new BackgroundScanOutcome(
                BackgroundScanStatus.CompletedAmbiguous,
                null,
                choices,
                failureReason);
        }

        return new BackgroundScanOutcome(
            BackgroundScanStatus.CompletedFailed,
            null,
            null,
            failureReason);
    }

    /// <summary>
    /// 构造仅含身份、不含对齐变换的识别结果（后台扫描/候选预览产物）。
    /// OverlayTransform 保持 null：识别只确定地图身份，不对齐；
    /// 玩家开图消费时才执行对齐并填充变换。纯函数，供单测驱动。
    /// </summary>
    public static RuntimeMapRecognition BuildIdentityOnlyRecognition(
        MapRecord map,
        string floorKey,
        double confidence,
        Func<MapRecord, string, string> overlayPathResolver)
    {
        if (MapFloorRules.GetFloorProfile(map, floorKey) is null)
            floorKey = MapFloorRules.GetPrimaryFloorKey(map);
        return new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = overlayPathResolver(map, floorKey),
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floorKey,
                Confidence = confidence,
                IdentityConfidence = confidence,
                LocalizationConfidence = 0d,
                Source = MapRecognitionSource.Automatic,
                OverlayTransform = null
            }
        };
    }

    /// <summary>
    /// 把扫描候选列表包装为「无变换身份」的选择项。纯函数，供单测驱动；
    /// 不执行对齐。非法 MapId、无法解析的地图会跳过；无可用候选返回 null。
    /// </summary>
    public static IReadOnlyList<MapRecognitionChoice>? BuildBackgroundCandidateChoices(
        IEnumerable<MapCandidate> candidates,
        int maxCandidates,
        Func<Guid, MapRecord?> resolveMap,
        Func<MapRecord, string, double, RuntimeMapRecognition> identityBuilder,
        out string? failureReason)
    {
        var choices = new List<MapRecognitionChoice>();
        var order = 0;
        foreach (var candidate in candidates.Take(maxCandidates))
        {
            if (!Guid.TryParse(candidate.MapId, out var mapId))
                continue;
            var map = resolveMap(mapId);
            if (map is null)
                continue;
            var floorKey =
                MapFloorRules.GetFloorProfile(map, candidate.FloorKey) is null
                    ? MapFloorRules.GetPrimaryFloorKey(map)
                    : candidate.FloorKey;
            choices.Add(new MapRecognitionChoice
            {
                Recognition = identityBuilder(map, floorKey, candidate.Score),
                EvidenceScore = candidate.Score,
                IsReferenceOnly = false,
                PreferredOrder = order++
            });
        }

        if (choices.Count == 0)
        {
            failureReason = "识别失败：无可用候选地图。";
            return null;
        }

        failureReason = null;
        return choices;
    }

    /// <summary>
    /// 从后台扫描保存的候选种子中挑选与待对齐地图匹配的侧门种子。
    /// 只有「侧门扫描先验置信度 &gt; 0」的真实侧门种子才会命中——KEEP-1.0
    /// 兜底种子（SideEntranceScanPriorConfidence == 0）不满足，返回 null，
    /// 消费路径将回退到现有 Default 路由（session: null）行为。
    /// 纯函数，供单测驱动；不执行对齐。
    /// </summary>
    public static MapAlignmentSession? PickSideEntranceSeed(
        MapAlignmentSession? seed,
        RuntimeMapRecognition identity,
        string floorKey)
    {
        if (seed is null)
            return null;
        if (seed.MapId != identity.Map.Id)
            return null;
        if (seed.MapUpdatedAt != identity.Map.UpdatedAt)
            return null;
        if (!string.Equals(seed.FloorKey, floorKey, StringComparison.Ordinal))
            return null;
        if (seed.SideEntranceScanPriorConfidence <= 0d)
            return null;
        return seed;
    }
}
/*
 * 文件职责：BackgroundScanRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
