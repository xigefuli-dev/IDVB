namespace IDVBuff.Features.Maps;

/// <summary>Internal structure-registration safety rules; not user settings.</summary>
internal static class StructureRegistrationRules
{
    public const double MinimumUsableScale = 0.05d;
    public const double ScaleAgreementTolerance = 0.003d;
    public const double StrictChamferFactor = 0.90d;
    public const double StrictEdgeCoverageMargin = 0.07d;
    public const double StrictOccupancyMargin = 0.08d;
    public const double PartitionPenaltyWeight = 0.75d;
    public const double PriorDisagreementPenaltyWeight = 0.75d;
    public const double EccMinimumCorrelation = 0.60d;
    public const double MinimumPriorAgreement = 0.05d;
    public const double StrictPriorAgreement = 0.20d;
    public const double RefinementChamferFactor = 0.85d;
    public const double RefinementEdgeCoverageMargin = 0.10d;
    public const double RefinementOccupancyMargin = 0.10d;
    public const double MinimumReplacementMargin = 0.10d;
    public const double MinimumTrustedFeatureConsensus = 0.50d;

    /// <summary>
    /// Adjacent scale hypotheses that resolve to the same reference location
    /// are one alignment peak, not competing map locations.
    /// </summary>
    public static bool IsSameAlignmentBasin(
        MapStructureCandidate first,
        MapStructureCandidate second,
        MapStructureRegistrationTuning tuning)
    {
        var scaleTolerance = Math.Max(
            0.001d,
            Math.Max(first.Scale, second.Scale)
                * Math.Max(0.005d, tuning.ScaleSearchRadius));
        if (Math.Abs(first.Scale - second.Scale) > scaleTolerance)
            return false;

        var minimumDistance = Math.Max(10d, tuning.MinimumSpanPixels / 2d);
        var offsetDistance = Math.Sqrt(
            Math.Pow(first.OffsetX - second.OffsetX, 2d)
            + Math.Pow(first.OffsetY - second.OffsetY, 2d));
        var referenceDistance = Math.Sqrt(
            Math.Pow(first.ReferenceX - second.ReferenceX, 2d)
            + Math.Pow(first.ReferenceY - second.ReferenceY, 2d));
        return offsetDistance < minimumDistance
            || referenceDistance < minimumDistance;
    }
}
