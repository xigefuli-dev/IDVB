using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public sealed record GroundTruthSample(
    string Id,
    string SourceType,
    string ReferenceName,
    string FloorKey,
    Mat LiveImage,
    Mat ReferenceStructureLine,
    Mat GroundTruthVisibleEdge,
    double TrueScale,
    double TrueOffsetX,
    double TrueOffsetY,
    MapScreenRect ViewportBounds,
    Rect QueryBounds,
    double FogFraction,
    bool HasDynamicExclusion,
    bool IsAmbiguous) : IDisposable
{
    public void Dispose()
    {
        LiveImage.Dispose();
        ReferenceStructureLine.Dispose();
        GroundTruthVisibleEdge.Dispose();
    }
}

public sealed record ScaleBenchmarkResult(
    string Algorithm,
    string SampleId,
    string SourceType,
    double EstimatedScale,
    double TrueScale,
    double ScaleError,
    double NormalizedMargin,
    double FwhmLogScale,
    double ElapsedMs,
    bool Success);

public sealed record ScaleFailureDetail(
    string SampleId,
    string Algorithm,
    string SourceType,
    double GroundTruthScale,
    double EstimatedScale,
    double AbsoluteError,
    string SearchDomain,
    string FailureReason);

public sealed record IdvaAblationResult(
    string StageName,
    string SampleId,
    double ElapsedMs,
    int EdgePixelCount,
    double PrecisionVsBaseline,
    double RecallVsBaseline);

public sealed record TranslationBenchmarkResult(
    string Algorithm,
    string SampleId,
    double EstimatedOffsetX,
    double EstimatedOffsetY,
    double TrueOffsetX,
    double TrueOffsetY,
    double ErrorPixels,
    int CollisionCount,
    double CandidateMargin,
    double ElapsedMs,
    bool FogSurvived);

public sealed record VerificationBenchmarkResult(
    string Method,
    string SampleId,
    double Score,
    bool Accepted,
    bool ProductionAccepted,
    bool Agreement,
    double ElapsedMicroseconds);

public sealed record PeakRatioRocPoint(
    double Threshold,
    double GateCoverage,
    double ConditionalErrorP50,
    double ConditionalErrorP95,
    double ConditionalErrorMax,
    double CatastrophicErrorRate);

public sealed record IdvaDownstreamResult(
    string StageName,
    string SampleId,
    string SourceType,
    double ExtractionElapsedMs,
    int EdgePixelCount,
    double PrecisionVsBaseline,
    double RecallVsBaseline,
    double PrecisionVsGroundTruth,
    double RecallVsGroundTruth,
    double DownstreamScaleError,
    double DownstreamTranslationError,
    bool Top1Hit3px,
    bool Top4Hit3px,
    int FalseCandidateCount);

public sealed record TranslationTopKResult(
    string Algorithm,
    string SampleId,
    string SourceType,
    double Top1ErrorPixels,
    bool Top1Hit2px,
    bool Top1Hit3px,
    bool Top1Hit5px,
    bool Top2Recall,
    bool Top4Recall,
    bool Top8Recall,
    double BestInTopKError,
    double CandidateMargin,
    double ElapsedMs);

public sealed record StrictVerificationResult(
    string Method,
    string SampleId,
    string SourceType,
    double Score,
    bool Accepted,
    bool IsActuallyCorrect,
    double ElapsedMicroseconds);

public sealed record PyramidBenchmarkResult(
    int DownsampleFactor,
    int ScaleLevels,
    long PreparedMemoryBytes,
    double BuildTimeMs,
    double ScaleErrorP50,
    double ScaleErrorP95,
    double TranslationTop1Recall,
    double TranslationTop4Recall,
    double RuntimeMsP50,
    double RuntimeMsP95);

public sealed record EndToEndResult(
    string SampleId,
    string SourceType,
    double ElapsedMs,
    double ScaleError,
    double TranslationError,
    bool FastAccepted,
    bool IsCorrectAccept,
    bool IsWrongAccept,
    bool IsFallback);
