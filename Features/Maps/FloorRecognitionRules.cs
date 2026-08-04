namespace IDVBuff.Features.Maps;

/// <summary>Internal floor-indicator rules; not persisted in settings.</summary>
internal static class FloorRecognitionRules
{
    public const double DefaultMinimumConfidence = 0.60d;
    public const double DefaultMinimumLocalizationConfidence = 0.70d;
    public const int DefaultRecognitionWindowMilliseconds = 3000;
    public const int DefaultFirstFloorConfirmationFrames = 2;
    public const int DefaultSecondFloorConfirmationFrames = 3;
    public const double Epsilon = 0.000001d;
    public const double MinimumTextureContrastFactor = 0.20d;
    public const double CannyLowThreshold = 30d;
    public const double CannyHighThreshold = 105d;
    public const double MeanContrastWeight = 0.15d;
    public const double DeviationContrastWeight = 0.55d;
    public const double GradientContrastWeight = 0.30d;
}
