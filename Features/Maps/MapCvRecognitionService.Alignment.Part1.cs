using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
public sealed partial class MapCvRecognitionService
{

    /// <summary>
    /// Solves the user's manually marked gate pair against one explicitly
    /// selected map. This is used when the chooser selection came from the
    /// catalog tail rather than the recognition candidate set.
    /// </summary>
    public RuntimeMapRecognition? RecognizeManualSelectedMap(
        Guid selectedMapId,
        MapScreenRect viewportBounds,
        MapScreenRect mainGateBounds,
        MapScreenRect sideGateBounds,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        out string failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == selectedMapId
            && string.Equals(
                item.FloorKey,
                MapFloorRules.GetPrimaryFloorKey(item.Map),
                StringComparison.Ordinal));
        if (fingerprint is null)
        {
            failureReason = "所选地图缺少可用的一楼双门识别配置。";
            return null;
        }
        if (!viewportBounds.IsValid
            || !mainGateBounds.IsValid
            || !sideGateBounds.IsValid)
        {
            failureReason = "手动框选的地图区域或门矩形无效。";
            return null;
        }

        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [
                new GateDetection
                {
                    Score = 1d,
                    ScreenBounds = mainGateBounds
                },
                new GateDetection
                {
                    Score = 1d,
                    ScreenBounds = sideGateBounds
                }
            ],
            viewportBounds,
            double.PositiveInfinity,
            testSwappedAssignments: false);
        var selected = ranked.FirstOrDefault();
        if (selected is null)
        {
            failureReason = "所选地图无法与手动框选的双门建立几何关系。";
            return null;
        }

        if (!MapCvRecognitionBuilders.TryBuildRecognition(
                selected,
                alignmentMode,
                tuning,
                double.PositiveInfinity,
                usedConfirmation: false,
                MapRecognitionSource.ManualGateSelection,
                wasForcedBestResult: false,
                out var recognition,
                out failureReason))
        {
            return null;
        }

        return recognition;
    }

    internal IReadOnlyList<MapGeometryFingerprint> FilterFingerprints(string? mapClass)
    {
        if (string.IsNullOrWhiteSpace(mapClass))
            return _fingerprints;

        return _fingerprints
            .Where(fingerprint => string.Equals(
                fingerprint.Map.Class,
                mapClass,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static RuntimeMapRecognition ConfirmChoice(MapRecognitionChoice choice)
    {
        var original = choice.Recognition;
        var result = original.Result;
        return new RuntimeMapRecognition
        {
            Map = original.Map,
            FloorImagePath = original.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = 0,
                Confidence = result.Confidence,
                IdentityConfidence = result.IdentityConfidence,
                LocalizationConfidence = result.LocalizationConfidence,
                Source = MapRecognitionSource.UserConfirmed,
                HasAllRequiredAnchorEvidence = result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation =
                    result.SkippedStructureValidation,
                WasForcedBestResult = result.WasForcedBestResult,
                ReusedLastTransform = result.ReusedLastTransform,
                UsedCachedScale = result.UsedCachedScale,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin = result.StructureCandidateMargin,
                StructureRejectionReason = result.StructureRejectionReason
            }
        };
    }

}
