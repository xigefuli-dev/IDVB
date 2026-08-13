using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void LogOrbMetrics(
        OrbTrackingContext context,
        string outcome,
        double captureMilliseconds,
        double orbMilliseconds,
        double structureMilliseconds,
        int weakFrames,
        string reason,
        MapOrbTrackingResult? result = null)
    {
        _logCollector.Append(
            MapLogCategory.OrbTracking,
            MapLogLevel.Info,
            $"ORB tracking sample · {outcome}",
            elapsedMs: captureMilliseconds + orbMilliseconds + structureMilliseconds,
            details: new()
            {
                ["generation"] = context.Generation,
                ["captureMs"] = captureMilliseconds,
                ["orbMs"] = orbMilliseconds,
                ["featureExtractionMs"] = result?.FeatureExtractionMilliseconds ?? 0,
                ["matchingMs"] = result?.MatchingMilliseconds ?? 0,
                ["ransacMs"] = result?.RansacMilliseconds ?? 0,
                ["structureCorrectionMs"] = structureMilliseconds,
                ["weakFrames"] = weakFrames,
                ["matches"] = result?.MatchCount ?? 0,
                ["inliers"] = result?.InlierCount ?? 0,
                ["inlierRatio"] = result?.InlierRatio ?? 0,
                ["medianReprojectionErrorPx"] = result?.MedianReprojectionErrorPixels,
                ["reason"] = reason
            });
    }

    private static double ElapsedMilliseconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;
}
