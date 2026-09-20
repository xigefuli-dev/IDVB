using IDVBuff.Core.Contracts;
using OpenCvSharp;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IGateDetector 适配器 — 委托给 GateTemplateDetector，
/// 并将 Features.GateDetection 映射为 Core.Models.GateDetection。</summary>
public sealed class GateDetectorAdapter : IGateDetector
{
    private readonly GateTemplateDetector _detector;

    public GateDetectorAdapter(string gateTemplatePath)
    {
        _detector = new GateTemplateDetector(gateTemplatePath);
    }

    public bool HasWarmScale => _detector.HasWarmScale;
    public double? WarmScale => _detector.WarmScale;

    public IReadOnlyList<object> Detect(object liveMatchImage, object viewportBounds,
        double clientWidth = 1920d, double scoreThreshold = 0.6d)
    {
        using var gray = GateTemplateDetector.CreateMatchImage((Mat)liveMatchImage);
        var result = _detector.Detect(
            gray,
            (MapScreenRect)viewportBounds,
            clientWidth,
            scoreThreshold);
        return MapToCoreModels(result);
    }

    public object Detect(object liveMatchImage, object viewportBounds,
        double clientWidth, double scoreThreshold, object? searchContext)
    {
        using var gray = GateTemplateDetector.CreateMatchImage((Mat)liveMatchImage);
        var result = _detector.Detect(
            gray,
            (MapScreenRect)viewportBounds,
            clientWidth,
            scoreThreshold,
            (GateSearchContext?)searchContext);
        return MapToCoreModels(result.Gates);
    }

    /// <summary>将 Features.Maps.GateDetection → Core.Models.GateDetection，
    /// 避免 Pipeline 层 `is IReadOnlyList&lt;GateDetection&gt;` 跨程序集类型不匹配。</summary>
    private static IReadOnlyList<Core.Models.GateDetection> MapToCoreModels(
        IReadOnlyList<GateDetection> source)
    {
        var mapped = new List<Core.Models.GateDetection>(source.Count);
        foreach (var gate in source)
        {
            var bounds = gate.ScreenBounds;
            mapped.Add(new Core.Models.GateDetection
            {
                Score = gate.Score,
                TemplateScale = gate.Scale,
                ScreenBounds = new Core.Models.ViewportBounds(
                    bounds.X, bounds.Y, bounds.Width, bounds.Height),
            });
        }
        return mapped;
    }

    public void RememberSuccessfulScale(double scale) =>
        _detector.RememberSuccessfulScale(scale);

    public void ResetSuccessfulScale() =>
        _detector.ResetSuccessfulScale();

    public void Dispose() => _detector.Dispose();
}
/*
 * 文件职责：GateDetectorAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
