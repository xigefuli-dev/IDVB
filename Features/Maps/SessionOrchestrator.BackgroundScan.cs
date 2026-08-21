// IDVB Remaster — 后台扫描（Background Scan）状态编排

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private BackgroundScanStatus _backgroundScanStatus;
    private RuntimeMapRecognition? _pendingBackgroundIdentity;
    private IReadOnlyList<MapRecognitionChoice>? _pendingBackgroundChoices;
    private string _pendingBackgroundChoicesReason = string.Empty;
    private string? _pendingBackgroundFailureReason;
    // 侧门策略下的后台扫描：识别即对齐，侧门种子（SideEntranceScanPriorConfidence>0）
    // 随身份一并保存，开图消费时用其走侧门路由（Default 自动升级 SideEntrance）。
    private MapAlignmentSession? _pendingBackgroundSeed;
    // 侧门扫描结果：歧义路径（多个可靠候选）下扫描不产单一侧门种子，
    // 消费候选确认后需用保存的扫描结果为选中的候选重建种子，与前台
    // 候选确认（Recognition.cs:123-255）语义一致。
    private SideEntranceScanResult? _pendingBackgroundScan;

    /// <summary>当前后台扫描状态（供状态栏 / CLI 展示）。</summary>
    public BackgroundScanStatus BackgroundScanStatus => _backgroundScanStatus;

    /// <summary>后台扫描是否已完成且结果尚未被开图消费。</summary>
    public bool IsBackgroundScanCompleted =>
        _backgroundScanStatus is BackgroundScanStatus.CompletedIdentified
            or BackgroundScanStatus.CompletedAmbiguous;

    /// <summary>后台扫描确定的候选身份；已完成且未消费时非空。</summary>
    public RuntimeMapRecognition? PendingBackgroundIdentity =>
        _pendingBackgroundIdentity;

    /// <summary>
    /// 后台扫描完成后保存待消费结果并标记状态。不弹候选/缩放界面、不对齐、
    /// 不提交 overlay——全部延迟到玩家第一次打开游戏地图时消费。
    /// </summary>
    private void CompleteBackgroundScan(InitialRecognitionPipelineState state)
    {
        if (state.ScanSucceeded)
        {
            // 后台扫描不改变游戏地图的物理开关状态：侧门识别作用于游戏世界中
            // 的门特征（无需打开大地图），玩家按快捷扫描键时地图通常处于关闭
            // 状态。因此不预置 _gameMapToggleState 为打开——否则玩家第一次按键
            // 会被 Toggle() 翻转为「关闭」（不消费），第二次才「打开」（消费）。
            // 保持扫描前的开关状态，玩家第一次打开地图（关→开）即触发消费。
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "后台扫描成功；玩家第一次打开游戏地图时消费对齐。");
        }

        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            state.Recognition,
            state.PendingChoices,
            state.FailureReason);
        _pendingBackgroundIdentity = outcome.Identity;
        _pendingBackgroundChoices = outcome.Choices;
        _pendingBackgroundChoicesReason = state.PendingChoicesReason;
        _pendingBackgroundFailureReason = outcome.FailureReason;
        // 侧门策略下识别即对齐：随身份保存侧门种子，供开图消费走侧门路由。
        // 歧义路径种子为 null，但侧门扫描结果保留候选特征，消费候选确认后重建。
        _pendingBackgroundSeed = state.PendingSideEntranceSeed;
        _pendingBackgroundScan = state.PendingSideEntranceScan;
        _backgroundScanStatus = outcome.Status;

        _statusMessage = outcome.Status switch
        {
            BackgroundScanStatus.CompletedIdentified =>
                $"后台扫描完成：{outcome.Identity!.Map.DisplayName}（打开游戏地图后对齐）",
            BackgroundScanStatus.CompletedAmbiguous =>
                $"后台扫描完成：{outcome.Choices!.Count} 个候选地图待确认（打开游戏地图后选择）",
            _ => $"后台扫描未识别地图：{outcome.FailureReason ?? "未知原因"}"
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>作废未消费的后台扫描结果（关闭开关 / 重新扫描 / 对局结束）。</summary>
    private void ClearPendingBackgroundScan()
    {
        _backgroundScanStatus = BackgroundScanStatus.Idle;
        _pendingBackgroundIdentity = null;
        _pendingBackgroundChoices = null;
        _pendingBackgroundChoicesReason = string.Empty;
        _pendingBackgroundFailureReason = null;
        _pendingBackgroundSeed = null;
        _pendingBackgroundScan = null;
    }
}
/*
 * 文件职责：SessionOrchestrator.BackgroundScan。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
