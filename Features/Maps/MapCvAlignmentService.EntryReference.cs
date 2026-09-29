namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt AlignEntrySelectedFloor(MapCvRecognitionService service,
        CapturedGameFrame frame, MapRecord map, string floorKey, MapAlignmentSession? session,
        MapOverlayAlignmentMode alignmentMode, MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning, MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin, IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory)
    {
        var compatible = session is not null && session.MapId == map.Id && session.MapUpdatedAt == map.UpdatedAt
            && session.FloorKey == floorKey && session.LockedTransform.AlignmentMode == alignmentMode ? session : null;
        return service.AlignEntryFloor(frame, map.Id, floorKey, compatible?.LastConfidence ?? 0);
    }
}
