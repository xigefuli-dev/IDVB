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
