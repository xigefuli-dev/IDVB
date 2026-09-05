using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal string GetAlignmentReferencePath(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning) =>
        tuning.UsePrebuiltStructureLine
            ? Repository.GetPrebuiltStructureLinePath(map, floorKey)
            : Repository.GetFloorRecognitionPath(map, floorKey);

    internal static MapStructurePreprocessingProfile GetReferenceProfile(
        MapStructureRegistrationTuning tuning,
        MapStructurePreprocessingProfile regularProfile) =>
        tuning.UsePrebuiltStructureLine
            ? MapStructurePreprocessingProfile.PrebuiltStructureLine
            : regularProfile;

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
        var nativeObserved = frame.GetOrCreateNativeObservedStructure();
        using var computationEdges = new Mat();
        using var computationMask = new Mat();
        Cv2.Resize(
            nativeObserved.ObservedEdges,
            computationEdges,
            frame.ComputationImage.Size(),
            interpolation: InterpolationFlags.Nearest);
        Cv2.Resize(
            nativeObserved.ValidMask,
            computationMask,
            frame.ComputationImage.Size(),
            interpolation: InterpolationFlags.Nearest);

        computation = MapStructurePreprocessor.UseNativeObservedStructureLine(
            computationEdges,
            computationMask);
        original = MapStructurePreprocessor.UseNativeObservedStructureLine(
            nativeObserved.ObservedEdges,
            nativeObserved.ValidMask);
        timer.Stop();
        elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
}
