namespace IDVBuff.Features.Maps;

public static class MapAlignmentSearchPolicy
{
    public static bool UseTrackingForGlobalRecovery(
        AlignmentSearchContext? searchContext) =>
        searchContext?.UseInitialHighPrecisionRecovery != true;
}
