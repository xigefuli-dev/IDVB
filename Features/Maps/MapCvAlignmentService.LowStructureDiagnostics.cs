using OpenCvSharp;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static void AppendLowStructureInputContract(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        MapScaleSearchPolicy scaleSearchPolicy,
        LowStructureAlignmentPlan? plan,
        FloorRecognitionProfile profile)
    {
        var visibleMask = live.RawVisibleMask;
        var hasVisibleMask = visibleMask is { IsDisposed: false } && !visibleMask.Empty();
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "LowStructureInputContract",
            details: new()
            {
                ["frameId"] = frame.CaptureSystemRelativeTicks,
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["alignmentChannel"] = tuning.Channel.ToString(),
                ["usePrebuiltStructureLine"] = tuning.UsePrebuiltStructureLine,
                ["referenceSource"] = tuning.UsePrebuiltStructureLine
                    ? "PrebuiltStructureLine" : "RecognitionImage",
                ["referencePreprocessingProfile"] = reference.DiagnosticTiming?.Profile.ToString(),
                ["referenceGenerationFingerprint"] = reference.DiagnosticTiming?.GenerationFingerprint,
                ["referenceWidth"] = reference.Edges.Width,
                ["referenceHeight"] = reference.Edges.Height,
                ["referenceEdgeCount"] = Cv2.CountNonZero(reference.Edges),
                ["liveSource"] = tuning.UsePrebuiltStructureLine
                    ? "NativeObservedStructureLine" : "ProcessLiveRoi",
                ["livePreprocessingProfile"] = live.DiagnosticTiming?.Profile.ToString(),
                ["liveWidth"] = live.Edges.Width,
                ["liveHeight"] = live.Edges.Height,
                ["originalLiveWidth"] = frame.Image.Width,
                ["originalLiveHeight"] = frame.Image.Height,
                ["physicalPixelsPerComputationPixel"] = frame.PhysicalPixelsPerComputationPixel,
                ["hasRawVisibleMask"] = hasVisibleMask,
                ["visibleMaskPixelCount"] = hasVisibleMask ? Cv2.CountNonZero(visibleMask!) : 0,
                ["validMapBounds"] = profile.GetEffectiveValidMapBounds(
                    reference.Edges.Width, reference.Edges.Height),
                ["scaleSearchPolicy"] = scaleSearchPolicy.ToString(),
                ["scaleSeed"] = plan?.Scales.FirstOrDefault(),
                ["lowStructurePlanRoute"] = plan?.Route.ToString(),
                ["plannedScales"] = plan is null ? [] : plan.Scales.ToArray(),
                ["translationTopK"] = tuning.LowStructureTranslationTopK,
                ["maximumTranslationCandidates"] = tuning.FastCoarseTopK,
                ["topCandidateCount"] = tuning.TopCandidateCount,
                ["computationSpace"] = $"{frame.ComputationImage.Width}x{frame.ComputationImage.Height}",
                ["originalPixelAcceptanceEnabled"] = true
            });
    }

}
