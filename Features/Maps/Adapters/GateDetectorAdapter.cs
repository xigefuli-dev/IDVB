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
