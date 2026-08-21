using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void ShowAdaptiveProvisionalStatus(
        RuntimeMapRecognition recognition,
        AdaptiveAlignmentDecision decision,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle)
    {
        _statusMessage =
            $"临时对齐：{recognition.Map.DisplayName} · "
            + $"{recognition.Result.Floor.ToUpperInvariant()} · "
            + $"置信度 {recognition.Result.LocalizationConfidence:P0} · "
            + $"连续高质量 {decision.ConsecutiveHighQualityCount}/"
            + $"{decision.RequiredHighQualityCount}";
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Warning,
            "临时对齐",
            _statusMessage,
            $"置信度 {recognition.Result.LocalizationConfidence:P0} · "
            + $"连续高质量 {decision.ConsecutiveHighQualityCount}/"
            + $"{decision.RequiredHighQualityCount}",
            gameBounds,
            gameWindowHandle);
    }

    private void ShowAdaptiveReliableStatus(
        RuntimeMapRecognition recognition,
        AdaptiveAlignmentDecision decision,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle)
    {
        if (decision.ReliabilityReason == AdaptiveScaleReliabilityReason.Disabled)
        {
            ShowTransientAlignmentSuccess(recognition, gameBounds, gameWindowHandle);
            return;
        }
        var confidence = $"置信度 {recognition.Result.LocalizationConfidence:P0}";
        var detail = decision.ReliabilityReason switch
        {
            AdaptiveScaleReliabilityReason.InitialFiveStreak =>
                $"{confidence} · 连续高质量 "
                + $"{decision.ConsecutiveHighQualityCount}/{decision.RequiredHighQualityCount}",
            AdaptiveScaleReliabilityReason.StructureConsensus =>
                $"{confidence} · 多帧结构共识",
            _ => $"{confidence} · 已验证启动标定"
        };
        _statusMessage =
            $"可靠对齐：{recognition.Map.DisplayName} · "
            + $"{recognition.Result.Floor.ToUpperInvariant()} · {detail}";
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Success,
            "可靠对齐",
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}",
            detail,
            gameBounds,
            gameWindowHandle);
    }

    private void PublishAdaptiveReliableStatus(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition)
    {
        var gameBounds = frame.ClientBounds;
        var gameWindowHandle = frame.WindowHandle;
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (!IsOrbTrackingContextCurrent(context))
                return;
            _statusMessage =
                $"可靠对齐：{recognition.Map.DisplayName} · "
                + $"{recognition.Result.Floor.ToUpperInvariant()} · "
                + $"置信度 {recognition.Result.LocalizationConfidence:P0} · 多帧结构共识";
            ShowTransientOverlayStatus(
                MapOverlayStatusLevel.Success,
                "可靠对齐",
                $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}",
                $"置信度 {recognition.Result.LocalizationConfidence:P0} · 多帧结构共识",
                gameBounds,
                gameWindowHandle);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }))
        {
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "可靠对齐状态刷新未能加入 UI 队列。",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["confidence"] = recognition.Result.Confidence
                });
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleStatus。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
