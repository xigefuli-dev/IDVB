namespace IDVBuff.Features.Maps;

public sealed record MapVpsgStructureScaleEstimate(
    double Scale,
    double Confidence,
    double Cost,
    double Margin,
    int TestedScaleCount)
{
    public double? HintScale { get; init; }
    public double HintConfidence { get; init; }
    public double SearchMinimumScale { get; init; }
    public double SearchMaximumScale { get; init; }
    public IReadOnlyList<double> ScaleCandidates { get; init; } = [];
}

/// <summary>
/// Structure-only VPSG bootstrap. It estimates scale from edge geometry and
/// deliberately returns no translation; the registrar owns translation and
/// the fixed-scale acceptance decision.
/// </summary>
public sealed class MapVpsgStructureScaleEstimator
{
    private const int CoarseScaleCount = 17;
    private const int CoarseDownsampleFactor = 4;

    public bool TryEstimate(
        MapStructureFeatures reference,
        MapStructureFeatures live,
        double? priorScale,
        out MapVpsgStructureScaleEstimate? estimate,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(live);

        estimate = null;
        rejectionReason = string.Empty;
        if (reference.Edges.Empty() || live.Edges.Empty())
        {
            rejectionReason = "empty structure edges";
            return false;
        }

        var tuning = new MapStructureRegistrationTuning
        {
            Channel = MapAlignmentChannel.Standard,
            EnableFeatureVoting = false,
            LowStructureMinimumScale = MapFloorScaleSearchPolicy.UncalibratedMinimumScale,
            LowStructureMaximumScale = MapFloorScaleSearchPolicy.UncalibratedMaximumScale,
            LowStructureScaleHypothesisCount = CoarseScaleCount,
            FastCoarseDownsampleFactor = CoarseDownsampleFactor
        };
        tuning.Normalize();

        var selection = MapStructureScaleEstimator.Analyze(
            live,
            reference,
            tuning,
            includeAppearanceScale: false,
            preferredScale: priorScale,
            useScaleHint: true);
        if (selection.Scales.Count == 0
            || !double.IsFinite(selection.BestCost))
        {
            rejectionReason = "structure scale search produced no candidate";
            return false;
        }

        var scale = selection.Scales[0];
        if (!double.IsFinite(scale)
            || scale < MapFloorScaleSearchPolicy.UncalibratedMinimumScale
            || scale > MapFloorScaleSearchPolicy.UncalibratedMaximumScale)
        {
            rejectionReason = $"structure scale candidate is outside the search domain: {scale:F4}";
            return false;
        }

        var margin = double.IsFinite(selection.SecondCost)
            ? Math.Clamp(
                (selection.SecondCost - selection.BestCost)
                    / Math.Max(Math.Abs(selection.SecondCost), 0.05d),
                0d,
                1d)
            : 0d;
        // Confidence is bootstrap evidence only. Fixed-scale structure
        // validation remains the acceptance gate in AlignLockedFloorFeature.
        var costQuality = Math.Clamp(
            1d - (selection.BestCost / 12d),
            0d,
            1d);
        var confidence = Math.Clamp(
            0.45d + (costQuality * 0.30d) + (margin * 0.25d),
            0d,
            0.98d);
        estimate = new MapVpsgStructureScaleEstimate(
            scale,
            confidence,
            selection.BestCost,
            margin,
            selection.BasinCount)
        {
            HintScale = selection.HintScale,
            HintConfidence = selection.HintConfidence,
            SearchMinimumScale = selection.SearchMinimumScale,
            SearchMaximumScale = selection.SearchMaximumScale,
            ScaleCandidates = new[] { scale }
                .Concat((selection.TopBasinScales ?? []).Skip(1))
                .DistinctBy(candidate => Math.Round(candidate, 9))
                .Take(2)
                .ToArray()
        };
        return true;
    }

}
