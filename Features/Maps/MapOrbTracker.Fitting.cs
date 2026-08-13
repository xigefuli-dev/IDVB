using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapOrbTracker
{
    private static bool TryFitRotationFreeSimilarity(
        IReadOnlyList<Point2f> source,
        IReadOnlyList<Point2f> destination,
        IReadOnlyList<int> inliers,
        out double scale,
        out double translationX,
        out double translationY,
        out double medianError)
    {
        scale = translationX = translationY = medianError = double.NaN;
        if (inliers.Count < 3)
            return false;
        var sourceMeanX = inliers.Average(index => (double)source[index].X);
        var sourceMeanY = inliers.Average(index => (double)source[index].Y);
        var destinationMeanX = inliers.Average(index => (double)destination[index].X);
        var destinationMeanY = inliers.Average(index => (double)destination[index].Y);
        double numerator = 0;
        double denominator = 0;
        foreach (var index in inliers)
        {
            var sx = source[index].X - sourceMeanX;
            var sy = source[index].Y - sourceMeanY;
            numerator += (sx * (destination[index].X - destinationMeanX))
                + (sy * (destination[index].Y - destinationMeanY));
            denominator += (sx * sx) + (sy * sy);
        }
        if (denominator <= 1e-6)
            return false;
        scale = numerator / denominator;
        translationX = destinationMeanX - (scale * sourceMeanX);
        translationY = destinationMeanY - (scale * sourceMeanY);
        var fittedScale = scale;
        var fittedTranslationX = translationX;
        var fittedTranslationY = translationY;
        var errors = inliers
            .Select(index =>
            {
                var dx = destination[index].X
                    - ((fittedScale * source[index].X) + fittedTranslationX);
                var dy = destination[index].Y
                    - ((fittedScale * source[index].Y) + fittedTranslationY);
                return Math.Sqrt((dx * dx) + (dy * dy));
            })
            .OrderBy(value => value)
            .ToArray();
        medianError = errors.Length % 2 == 0
            ? (errors[(errors.Length / 2) - 1] + errors[errors.Length / 2]) / 2d
            : errors[errors.Length / 2];
        return double.IsFinite(scale)
            && double.IsFinite(translationX)
            && double.IsFinite(translationY)
            && double.IsFinite(medianError);
    }
}
