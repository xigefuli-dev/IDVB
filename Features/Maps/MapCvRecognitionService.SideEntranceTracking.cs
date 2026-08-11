using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private const double LockedSideEntranceMinimumScore = 0.80d;
    private const double LockedSideEntranceMaximumScaleChangeRatio = 0.08d;

    /// <summary>
    /// Re-locates one already locked primary floor from its authored side
    /// feature. No other map participates, so this path cannot alter ranking
    /// or identity. A high feature score and a scale consistent with the
    /// existing lock only produce a same-frame transform proposal; static
    /// structure must independently validate it before it can be committed.
    /// </summary>
    public MapRecognitionAttempt AlignLockedSideEntranceFeature(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(session);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);

        var stopwatch = Stopwatch.StartNew();
        var candidate = RunSideEntranceScan(
                frame.Image,
                topK: 1,
                selectedMapId: selectedMapId)
            .FirstOrDefault(item => string.Equals(
                item.FloorKey,
                session.FloorKey,
                StringComparison.Ordinal));
        stopwatch.Stop();
        if (candidate is null)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map side feature was not visible.");

        var baselineScale = session.BaselineGateScale;
        var scaleChange = double.IsFinite(baselineScale) && baselineScale > 0d
            ? Math.Abs((candidate.MatchScale / baselineScale) - 1d)
            : double.PositiveInfinity;
        var minimumScore = Math.Max(
            LockedSideEntranceMinimumScore,
            tuning.MinimumConfidence);
        if (candidate.MatchScore < minimumScore
            || scaleChange > LockedSideEntranceMaximumScaleChangeRatio)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map side feature observation was not strong enough to seed structure validation.");
        }

        if (!SideEntranceScanPipeline.TryCreateAlignmentSeed(
                candidate,
                frame.ViewportBounds,
                out var seed,
                out var seedFailure))
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                seedFailure);
        }

        MapLogCollector.Instance.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"已锁定地图侧门特征提出结构验证种子 · score={candidate.MatchScore:P0} · "
            + $"scale={candidate.MatchScale:F3}",
            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = selectedMapId,
                ["floor"] = session.FloorKey,
                ["matchScore"] = candidate.MatchScore,
                ["matchScale"] = candidate.MatchScale,
                ["scaleChange"] = scaleChange
            });
        var searchContext = CreateSideEntranceWarmSearchContext(
            seed,
            tuning,
            useInitialHighPrecisionRecovery: true);
        return AlignSideEntrance(
            frame,
            selectedMapId,
            seed,
            alignmentMode,
            tuning,
            structureTuning,
            alignmentSearchContext: searchContext);
    }
}
