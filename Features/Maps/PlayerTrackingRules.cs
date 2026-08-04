namespace IDVBuff.Features.Maps;

/// <summary>Internal player-marker matching rules; not persisted in settings.</summary>
internal static class PlayerTrackingRules
{
    public const double DefaultMinimumConfidence = 0.70d;
    public const int DefaultLocalSearchFailureLimit = 5;
    public const int DefaultStaleHideMilliseconds = 500;
    public static readonly double[] TemplateScaleCandidates =
    [0.70d, 0.80d, 0.90d, 1.00d, 1.10d, 1.20d, 1.35d, 1.50d];
    public const double TemplateScoreWeight = 0.55d;
    public const double ColorAgreementWeight = 0.30d;
    public const double ShapeAgreementWeight = 0.15d;
    public const double MinimumTemplateScore = 0.65d;
    public const double MinimumColorAgreement = 0.45d;
    public const double MinimumShapeTolerance = 0.15d;
}
