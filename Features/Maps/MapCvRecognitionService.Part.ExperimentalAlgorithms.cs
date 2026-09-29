using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal bool HasPrebuiltStructureLine(MapRecord map, string floorKey) =>
        Repository.ResolveStructureLineReference(map, floorKey) is not null;

    internal bool UsesStructureLineReference(MapRecord map, string floorKey, MapStructureRegistrationTuning tuning) =>
        map.Floors.Any(floor => floor.Key == floorKey && floor.EntryIdentityAsset is not null)
        || tuning.UsePrebuiltStructureLine && HasPrebuiltStructureLine(map, floorKey);

    internal string GetAlignmentReferencePath(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning) =>
        UsesStructureLineReference(map, floorKey, tuning)
            ? Repository.ResolveStructureLineReference(map, floorKey)!.Path
            : Repository.GetFloorRecognitionPath(map, floorKey);

    internal bool TryGetAlignmentReferencePath(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning,
        out string path)
    {
        if (UsesStructureLineReference(map, floorKey, tuning))
        {
            path = Repository.ResolveStructureLineReference(map, floorKey)!.Path;
            return true;
        }
        path = Repository.GetFloorRecognitionPath(map, floorKey);
        return File.Exists(path);
    }

    internal static MapStructurePreprocessingProfile GetReferenceProfile(
        MapStructureRegistrationTuning tuning,
        MapStructurePreprocessingProfile regularProfile) =>
        tuning.UsePrebuiltStructureLine
            ? MapStructurePreprocessingProfile.PrebuiltStructureLine
            : regularProfile;

    internal MapStructurePreprocessingProfile GetReferenceProfile(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning,
        MapStructurePreprocessingProfile regularProfile) =>
        UsesStructureLineReference(map, floorKey, tuning)
            ? MapStructurePreprocessingProfile.PrebuiltStructureLine
            : regularProfile;

    internal MapStructureFeatures PrepareAlignmentReference(MapRecord map, string floorKey,
        Mat image, IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        MapStructureGenerationTuning generation, MapStructurePreprocessingProfile profile)
    {
        using var unknown = Repository.LoadStructureReferenceUnknown(map, floorKey);
        return StructureCache.GetOrCreate(map.Id, map.UpdatedAt, image, ignoreRegions,
            floorKey, generation, profile, unknown);
    }

    internal static VpsgScaleMode GetVpsgMode(
        MapStructureRegistrationTuning tuning) =>
        tuning.UsePrebuiltStructureLine
            ? VpsgScaleMode.Structure
            : Enum.IsDefined(tuning.VpsgScaleMode)
                ? tuning.VpsgScaleMode
                : VpsgScaleMode.Structure;

    internal void CreatePrebuiltLiveStructureFeatures(
        CapturedGameFrame frame,
        out MapStructureFeatures computation,
        out MapStructureFeatures original,
        out double elapsedMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        var shared = ScanExecutionContext.Current is { IsAutomatic: true, Frame: { } evidence }
            && ReferenceEquals(evidence.Source, frame.Image) ? evidence.Observation : null;
        var nativeObserved = shared is null ? frame.GetOrCreateNativeObservedStructure() : null;
        var observedEdges = shared?.ObservedEdges ?? nativeObserved!.ObservedEdges;
        var validMask = shared?.ValidMask ?? nativeObserved!.ValidMask;
        using var computationEdges = new Mat();
        using var computationMask = new Mat();
        Cv2.Resize(
            observedEdges,
            computationEdges,
            frame.ComputationImage.Size(),
            interpolation: InterpolationFlags.Nearest);
        Cv2.Resize(
            validMask,
            computationMask,
            frame.ComputationImage.Size(),
            interpolation: InterpolationFlags.Nearest);

        computation = MapStructurePreprocessor.UseNativeObservedStructureLine(
            computationEdges,
            computationMask);
        original = MapStructurePreprocessor.UseNativeObservedStructureLine(
            observedEdges,
            validMask);
        timer.Stop();
        elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
}
