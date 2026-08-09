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
    /// existing lock are sufficient alignment evidence; weaker observations
    /// fall back to the normal gate/structure pipeline.
    /// </summary>
    public MapRecognitionAttempt AlignLockedSideEntranceFeature(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapRecognitionTuning tuning)
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
                "The locked map side feature observation was not strong enough for direct alignment.");
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

        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == selectedMapId
            && string.Equals(
                item.FloorKey,
                session.FloorKey,
                StringComparison.Ordinal));
        if (fingerprint is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map side feature is no longer in the map cache.");
        }

        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.AuxiliaryAnchorTracking;
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.None;
        diagnostics.SkippedStructureValidation = true;
        diagnostics.StructureAttempted = false;
        var confidence = Math.Max(
            session.SideEntranceScanPriorConfidence,
            candidate.MatchScore);
        var recognition = MapCvRecognitionBuilders.BuildTrackedRecognition(
            fingerprint,
            seed.LockedTransform,
            [],
            MapRecognitionSource.SideEntranceSelection,
            confidenceOverride: confidence,
            evidenceKind: MapAlignmentEvidenceKind.None);

        MapLogCollector.Instance.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"已锁定地图侧门特征直接对齐 · score={candidate.MatchScore:P0} · "
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
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Recognition = recognition,
            SearchStage = AlignmentSearchStage.WarmGateSearch,
            StructureAttempted = false,
            StructureAccepted = false
        };
    }
}
