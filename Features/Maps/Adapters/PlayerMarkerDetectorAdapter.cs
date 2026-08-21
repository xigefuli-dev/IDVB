using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IPlayerMarkerDetector 适配器 — 委托给 MapPlayerMarkerDetector。</summary>
public sealed class PlayerMarkerDetectorAdapter : IPlayerMarkerDetector
{
    private readonly MapPlayerMarkerDetector _detector = new();

    public object Detect(object liveViewport, object viewportBounds, object clientBounds,
        object playerSlot, string templatePath, object? previousPoint, object? tuning = null)
    {
        return _detector.Detect(
            (OpenCvSharp.Mat)liveViewport,
            (MapScreenRect)viewportBounds,
            (MapScreenRect)clientBounds,
            (PlayerSlot)playerSlot,
            templatePath,
            (MapViewportPoint?)previousPoint,
            (MapPlayerTrackingTuning?)tuning);
    }

    public void ResetTracking() => _detector.ResetTracking();
    public void Dispose() => _detector.Dispose();
}
/*
 * 文件职责：PlayerMarkerDetectorAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
