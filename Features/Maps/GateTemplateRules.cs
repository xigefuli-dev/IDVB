namespace IDVBuff.Features.Maps;

/// <summary>Internal gate-detector rules; never persisted as user settings.</summary>
internal static class GateTemplateRules
{
    public const double EarlyExitScoreThreshold = 0.85d;
    public const double NmsIouThreshold = 0.25d;
    public const double SpatialClusterIouThreshold = 0.35d;
    public const int MaximumGateCandidates = 6;
    public const double ReferenceClientWidth = 2560d;
    public const double ReferenceScale = 0.275d;
    public const double CannyLowThreshold = 45d;
    public const double CannyHighThreshold = 135d;
    public const double WarmScaleStart = 0.85d;
    public const double WarmScaleStep = 0.075d;
    public const double WarmScaleMaximum = 1.15d;
    public const double SingleGateScaleTolerance = 0.15d;
    public const double SingleGateAmbiguityGap = 0.08d;
}
