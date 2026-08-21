using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IMapIdentifier 适配器 — 包装 MapCvRecognitionScript.RankGeometry 静态方法。
/// 将 Core.Models.GateDetection 反向映射为 Features.Maps.GateDetection。</summary>
public sealed class MapIdentifierAdapter : IMapIdentifier
{
    public IReadOnlyList<object> RankGeometry(
        IReadOnlyList<object> fingerprints,
        IReadOnlyList<object> gates,
        object viewportBounds,
        double vectorErrorTolerance = -1d,
        bool testSwappedAssignments = true)
    {
        var fps = fingerprints.Cast<MapGeometryFingerprint>().ToList();
        var bounds = (MapScreenRect)viewportBounds;

        // 将 Core.Models.GateDetection 映射回 Features.Maps.GateDetection
        static GateDetection MapGate(object g)
        {
            if (g is GateDetection fmg)
                return fmg;
            if (g is Core.Models.GateDetection cmg)
            {
                var vb = cmg.ScreenBounds;
                return new GateDetection
                {
                    Score = cmg.Score,
                    Scale = cmg.TemplateScale,
                    ScreenBounds = new MapScreenRect(vb.X, vb.Y, vb.Width, vb.Height),
                };
            }
            // 从未知类型退化为空门
            return new GateDetection { Score = 0, Scale = 0 };
        }

        var gs = gates.Select(MapGate).ToList();

        var results = MapCvRecognitionScript.RankGeometry(
            fps, gs, bounds, vectorErrorTolerance, testSwappedAssignments);

        return results!;
    }
}
/*
 * 文件职责：MapIdentifierAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
