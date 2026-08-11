using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Fusion.OpenCv;

internal sealed record SurveyFusionLayout(
    SurveyWorldRect Bounds,
    SurveyWorldPoint Origin,
    Size CanvasSize);

internal static class SurveyFusionGeometry
{
    public static SurveyFusionLayout Calculate(
        IReadOnlyList<SurveyMapLayer> layers,
        IReadOnlyDictionary<Guid, SurveyObservation> observations,
        int maximumPixels)
    {
        var points = new List<SurveyWorldPoint>(layers.Count * 4);
        foreach (var layer in layers)
        {
            if (!observations.TryGetValue(layer.ObservationId, out var observation))
                continue;
            var width = observation.SourceAsset.PixelWidth;
            var height = observation.SourceAsset.PixelHeight;
            var transform = layer.EffectiveTransform;
            points.Add(transform.Transform(new SurveyWorldPoint(0d, 0d)));
            points.Add(transform.Transform(new SurveyWorldPoint(width, 0d)));
            points.Add(transform.Transform(new SurveyWorldPoint(0d, height)));
            points.Add(transform.Transform(new SurveyWorldPoint(width, height)));
        }
        if (points.Count == 0)
            throw new InvalidOperationException("当前楼层没有可合成的测绘图层。");
        var minX = Math.Floor(points.Min(point => point.X));
        var minY = Math.Floor(points.Min(point => point.Y));
        var maxX = Math.Ceiling(points.Max(point => point.X));
        var maxY = Math.Ceiling(points.Max(point => point.Y));
        var widthPixels = checked((int)Math.Max(1d, maxX - minX));
        var heightPixels = checked((int)Math.Max(1d, maxY - minY));
        if ((long)widthPixels * heightPixels > maximumPixels)
            throw new InvalidOperationException("测绘世界画布超过配置允许的最大输出尺寸。");
        return new SurveyFusionLayout(
            new SurveyWorldRect(minX, minY, widthPixels, heightPixels),
            new SurveyWorldPoint(-minX, -minY),
            new Size(widthPixels, heightPixels));
    }

    public static Mat CreateAffine(SurveyLayerTransform transform, SurveyWorldPoint origin)
    {
        var radians = transform.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return Mat.FromArray(new[,]
        {
            {
                transform.ScaleX * cosine,
                -transform.ScaleY * sine,
                transform.TranslationX + origin.X
            },
            {
                transform.ScaleX * sine,
                transform.ScaleY * cosine,
                transform.TranslationY + origin.Y
            }
        });
    }
}
