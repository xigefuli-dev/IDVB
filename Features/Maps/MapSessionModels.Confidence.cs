namespace IDVBuff.Features.Maps;

public sealed class MapRegistrationConfidenceEvidence
{
    public double? AnchorGeometry { get; init; }
    public double? FeatureConsensus { get; init; }
    public double? CandidateSeparation { get; init; }
    public double? StructureQuality { get; init; }
    public double? RefinementQuality { get; init; }
    public double? BoundsAndPrior { get; init; }
    public double? TemporalStability { get; init; }

    public double Calculate()
    {
        var evidence = new[]
        {
            (AnchorGeometry, MapSessionRules.WeightAnchorGeometry),
            (FeatureConsensus, MapSessionRules.WeightFeatureConsensus),
            (CandidateSeparation, MapSessionRules.WeightCandidateSeparation),
            (StructureQuality, MapSessionRules.WeightStructureQuality),
            (RefinementQuality, MapSessionRules.WeightRefinementQuality),
            (BoundsAndPrior, MapSessionRules.WeightBoundsAndPrior),
            (TemporalStability, MapSessionRules.WeightTemporalStability)
        };
        var available = evidence
            .Where(item => item.Item1 is { } value && double.IsFinite(value))
            .ToArray();
        if (available.Length == 0)
            return 0d;
        var weight = available.Sum(item => item.Item2);
        return Math.Clamp(
            available.Sum(item => Math.Clamp(item.Item1!.Value, 0d, 1d) * item.Item2)
                / weight,
            0d,
            1d);
    }
}
