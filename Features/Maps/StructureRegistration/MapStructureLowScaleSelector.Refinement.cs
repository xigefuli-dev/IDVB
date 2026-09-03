namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleEstimator
{
    private static (double Scale, double Cost) FitLogScaleMinimum(
        IReadOnlyList<ScaleScore> scores,
        IReadOnlyList<double> grid,
        double fallbackScale)
    {
        var orderedGrid = grid.OrderBy(scale => scale).ToArray();
        var centerIndex = Array.FindIndex(
            orderedGrid,
            scale => Math.Abs(scale - fallbackScale) < 1e-9d);
        if (centerIndex <= 0 || centerIndex + 1 >= orderedGrid.Length)
            return (fallbackScale, scores[0].Cost);

        var leftScale = orderedGrid[centerIndex - 1];
        var centerScale = orderedGrid[centerIndex];
        var rightScale = orderedGrid[centerIndex + 1];
        var leftCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - leftScale) < 1e-9d)?.Cost ?? double.NaN;
        var centerCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - centerScale) < 1e-9d)?.Cost ?? double.NaN;
        var rightCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - rightScale) < 1e-9d)?.Cost ?? double.NaN;
        if (!double.IsFinite(leftCost)
            || !double.IsFinite(centerCost)
            || !double.IsFinite(rightCost))
        {
            return (fallbackScale, scores[0].Cost);
        }

        var leftLog = Math.Log(leftScale / centerScale);
        var rightLog = Math.Log(rightScale / centerScale);
        var leftSlope = (centerCost - leftCost) / -leftLog;
        var rightSlope = (rightCost - centerCost) / rightLog;
        var curvature = (rightSlope - leftSlope) / (rightLog - leftLog);
        if (!double.IsFinite(curvature) || curvature <= 1e-9d)
            return (fallbackScale, scores[0].Cost);

        var linear = leftSlope - (curvature * leftLog);
        var fittedLogOffset = Math.Clamp(
            -linear / (2d * curvature), leftLog, rightLog);
        var fittedScale = centerScale * Math.Exp(fittedLogOffset);
        var fittedCost = (curvature * fittedLogOffset * fittedLogOffset)
            + (linear * fittedLogOffset) + centerCost;
        return double.IsFinite(fittedScale) && double.IsFinite(fittedCost)
            ? (fittedScale, fittedCost)
            : (fallbackScale, scores[0].Cost);
    }
}
