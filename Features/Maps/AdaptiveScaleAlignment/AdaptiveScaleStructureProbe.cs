namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

/// <summary>
/// Orchestrates fine scale passes while the existing registrar owns feature
/// extraction, scoring, validation and translation refinement.
/// </summary>
internal sealed class AdaptiveScaleStructureProbe
{
    public MapRecognitionAttempt Refine(
        RuntimeMapRecognition seed,
        Func<RuntimeMapRecognition, double, MapRecognitionAttempt> runPass)
    {
        var first = runPass(seed, 0.02d);
        if (first.Recognition is not { } firstRecognition)
            return first;

        var second = runPass(firstRecognition, 0.005d);
        return second.Recognition is not null ? second : first;
    }
}
/*
 * 文件职责：AdaptiveScaleStructureProbe。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
