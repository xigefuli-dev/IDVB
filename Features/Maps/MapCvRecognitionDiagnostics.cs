using System.Text.Json;

namespace IDVBuff.Features.Maps;

internal static class MapCvRecognitionDiagnostics
{
    internal static MapScanDiagnostics CreateDiagnostics(int readyMapCount, int totalMapCount) =>
        new()
        {
            ReadyMapCount = readyMapCount,
            TotalMapCount = totalMapCount
        };

    internal static bool TryValidateRanking(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapRecognitionTuning tuning,
        MapScanDiagnostics diagnostics,
        out MapRecognitionAttempt? failure)
    {
        if (ranked.Count == 0)
        {
            failure = Failure(diagnostics, "没有可参与双门几何排名的地图。");
            return false;
        }

        if (ranked[0].VectorError > tuning.VectorErrorTolerance)
        {
            failure = GeometryFailure(
                diagnostics,
                ranked[0].VectorError,
                tuning.VectorErrorTolerance);
            return false;
        }

        failure = null;
        return true;
    }

    internal static MapRecognitionAttempt GeometryFailure(
        MapScanDiagnostics diagnostics,
        double error,
        double tolerance) =>
        Failure(
            diagnostics,
            $"地图区域或双门坐标不一致，请重新校准（误差 {error:F3}，阈值 {tolerance:F3}）。");

    internal static MapRecognitionAttempt Failure(
        MapScanDiagnostics diagnostics,
        string reason) =>
        new()
        {
            Diagnostics = diagnostics,
            FailureReason = reason
        };

    internal static void WriteStructureDebugResult(
        MapRecord map,
        MapStructureRegistrationResult result,
        string? singleGateFallbackReason)
    {
        if (string.IsNullOrWhiteSpace(result.DebugOutputDirectory))
            return;

        try
        {
            File.WriteAllText(
                Path.Combine(result.DebugOutputDirectory, "result.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        MapId = map.Id,
                        map.SequenceNumber,
                        result.Accepted,
                        Scale = result.Transform?.ScaleX,
                        result.Transform?.OffsetX,
                        result.Transform?.OffsetY,
                        result.Confidence,
                        result.BestScore,
                        SecondScore = double.IsFinite(result.SecondScore)
                            ? result.SecondScore
                            : (double?)null,
                        result.CandidateMargin,
                        RejectionReason = result.RejectionReason.ToString(),
                        result.FailureReason,
                        SingleGateFallbackReason = singleGateFallbackReason,
                        TopCandidates = result.Candidates,
                        Query = new
                        {
                            result.LockedScale,
                            ReferenceSize = new
                            {
                                Width = result.ReferenceWidth,
                                Height = result.ReferenceHeight
                            },
                            EdgePixels = result.QueryEdgePixels,
                            Bounds = new
                            {
                                X = result.QueryBoundsX,
                                Y = result.QueryBoundsY,
                                Width = result.QueryBoundsWidth,
                                Height = result.QueryBoundsHeight
                            },
                            result.ScaleHypothesisCount,
                            result.OversizedHypothesisCount,
                            result.UsedRestrictedSearch,
                            result.WasForcedBestCandidate
                        },
                        Timings = new
                        {
                            result.PreprocessMilliseconds,
                            result.SearchMilliseconds,
                            result.RefineMilliseconds
                        }
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must not change the acceptance decision.
        }
    }
}
