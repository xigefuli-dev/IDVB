namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal static bool CanUseVpsg3(
        MapAlignmentChannel channel,
        double? scaleSeed,
        bool hasValidatedFloorScale) =>
        channel == MapAlignmentChannel.Standard
        || (hasValidatedFloorScale && scaleSeed is { } scale
            && double.IsFinite(scale)
            && scale >= Vpsg3TuningConfig.Default.MinSupportedScale
            && scale <= Vpsg3TuningConfig.Default.MaxSupportedScale);

    internal static Vpsg3ScaleRefreshComparison CompareVpsg3ScaleRefresh(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor,
        Vpsg3BootstrapResult fresh,
        double prior)
    {
        Vpsg3BootstrapResult? bestPrior = null;
        Vpsg3BootstrapResult? baseline = null;
        var additionalMs = 0d;
        var testedScaleCount = fresh.TestedScaleHypotheses;
        foreach (var candidateScale in new[] { prior, prior * 0.995d, prior * 1.005d })
        {
            if (candidateScale < Vpsg3TuningConfig.Default.MinSupportedScale
                || candidateScale > Vpsg3TuningConfig.Default.MaxSupportedScale)
                continue;
            var candidate = Vpsg3FastBootstrapSolver.TrySolve(
                observation, floor, knownScaleSeed: candidateScale);
            additionalMs += Math.Max(0d,
                candidate.Timing.TotalMs - observation.ExtractionMilliseconds);
            testedScaleCount += candidate.TestedScaleHypotheses;
            if (baseline is null)
                baseline = candidate;
            // One sparse weighted hit is 1/450 at 150 points. Require about
            // seven extra vote units before replacing an accepted incumbent.
            if (candidate.IsAccepted
                && (bestPrior is null
                    || candidate.Confidence > bestPrior.Confidence + 0.015d))
                bestPrior = candidate;
        }

        var selectedPrior = bestPrior is not null
            && (!fresh.IsAccepted
                || fresh.Confidence <= bestPrior.Confidence + 0.015d);
        var selected = selectedPrior ? bestPrior! : fresh;
        additionalMs += fresh.Timing.TotalMs - selected.Timing.TotalMs;
        return new Vpsg3ScaleRefreshComparison(
            selected, baseline, bestPrior, selectedPrior,
            additionalMs, testedScaleCount);
    }
}

internal sealed record Vpsg3ScaleRefreshComparison(
    Vpsg3BootstrapResult Selected,
    Vpsg3BootstrapResult? Baseline,
    Vpsg3BootstrapResult? BestPrior,
    bool SelectedPriorHypothesis,
    double AdditionalMilliseconds,
    int TestedScaleCount);
