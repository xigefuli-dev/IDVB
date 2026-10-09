using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal bool HasPrebuiltStructureLine(MapRecord map, string floorKey) =>
        Repository.HasPrebuiltStructureLine(map, floorKey);

    internal string GetAlignmentReferencePath(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning) =>
        tuning.UsePrebuiltStructureLine && HasPrebuiltStructureLine(map, floorKey)
            ? Repository.GetPrebuiltStructureLinePath(map, floorKey)
            : Repository.GetFloorRecognitionPath(map, floorKey);

    internal bool TryGetAlignmentReferencePath(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning,
        out string path)
    {
        if (tuning.UsePrebuiltStructureLine && HasPrebuiltStructureLine(map, floorKey))
        {
            path = Repository.GetPrebuiltStructureLinePath(map, floorKey);
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
        tuning.UsePrebuiltStructureLine && HasPrebuiltStructureLine(map, floorKey)
            ? MapStructurePreprocessingProfile.PrebuiltStructureLine
            : regularProfile;

    internal static VpsgScaleMode GetVpsgMode(
        MapStructureRegistrationTuning tuning) =>
        tuning.UsePrebuiltStructureLine
            ? VpsgScaleMode.Structure
            : Enum.IsDefined(tuning.VpsgScaleMode)
                ? tuning.VpsgScaleMode
                : VpsgScaleMode.Structure;

    // Success returns a frame-owned pair; false leaves creation to the caller-owned factory.
    internal bool TryBorrowPrebuiltLiveStructureFeatures(
        CapturedGameFrame frame,
        out MapStructureFeatures computation,
        out MapStructureFeatures original,
        out bool cacheHit,
        out double originalExtractionMilliseconds)
    {
        var extractionTimer = Stopwatch.StartNew();
        var shared = ScanExecutionContext.Current is { IsAutomatic: true, Frame: { } evidence }
            && ReferenceEquals(evidence.Source, frame.Image) ? evidence.Observation : null;
        if (shared is not null)
        {
            extractionTimer.Stop();
            computation = null!;
            original = null!;
            cacheHit = false;
            originalExtractionMilliseconds = 0d;
            return false;
        }

        return frame.TryGetOrCreateNativePrebuiltLiveStructureFeatures(
            extractionTimer,
            out computation,
            out original,
            out cacheHit,
            out originalExtractionMilliseconds);
    }

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
