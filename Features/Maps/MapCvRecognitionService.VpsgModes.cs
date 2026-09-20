using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private sealed record VpsgBootstrapResult(
        VpsgScaleMode Mode,
        MapVpsgStructureScaleEstimate? Structure,
        MapVpsgScaleEstimate? Legacy,
        string StructureRejection,
        string LegacyRejection,
        double StructureMilliseconds,
        double LegacyMilliseconds);

    private VpsgBootstrapResult EstimateVpsgScales(
        MapRecord map,
        string floorKey,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        VpsgScaleMode mode,
        double? structurePriorScale,
        double legacyPriorScale)
    {
        MapVpsgStructureScaleEstimate? structure = null;
        MapVpsgScaleEstimate? legacy = null;
        var structureRejection = string.Empty;
        var legacyRejection = string.Empty;
        var structureMilliseconds = 0d;
        var legacyMilliseconds = 0d;

        if (mode is VpsgScaleMode.Structure or VpsgScaleMode.Both)
        {
            var timer = Stopwatch.StartNew();
            if (!new MapVpsgStructureScaleEstimator().TryEstimate(
                    reference,
                    live,
                    structurePriorScale,
                    out structure,
                    out structureRejection))
            {
                structure = null;
            }
            timer.Stop();
            structureMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        if (mode is VpsgScaleMode.LegacyAkaze or VpsgScaleMode.Both)
        {
            var timer = Stopwatch.StartNew();
            var graph = VpsgScaleGraphCache.GetOrCreate(
                map,
                floorKey,
                reference.Edges.Size(),
                reference.KeyPoints);
            if (!VpsgScaleEstimator.TryEstimate(
                    reference,
                    live,
                    graph,
                    legacyPriorScale,
                    out legacy,
                    out legacyRejection))
            {
                legacy = null;
            }
            timer.Stop();
            legacyMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        return new(
            mode,
            structure,
            legacy,
            structureRejection,
            legacyRejection,
            structureMilliseconds,
            legacyMilliseconds);
    }

    private static LockedFloorFeatureFit? CreateVpsgFit(
        VpsgBootstrapResult bootstrap,
        double physicalPixelsPerComputationPixel)
    {
        if (bootstrap.Mode == VpsgScaleMode.LegacyAkaze
            && bootstrap.Legacy is { } legacy)
        {
            return new LockedFloorFeatureFit(
                legacy.Scale * physicalPixelsPerComputationPixel,
                legacy.OffsetX * physicalPixelsPerComputationPixel,
                legacy.OffsetY * physicalPixelsPerComputationPixel,
                legacy.Evidence.UniqueMatches,
                legacy.Evidence.ResidualPixels * physicalPixelsPerComputationPixel,
                legacy.Evidence.ReferenceSpan,
                legacy.Evidence.LiveSpan * physicalPixelsPerComputationPixel,
                0d,
                legacy.Confidence);
        }

        if (bootstrap.Structure is { } structure)
        {
            return new LockedFloorFeatureFit(
                structure.Scale * physicalPixelsPerComputationPixel,
                0d,
                0d,
                0,
                0d,
                0d,
                0d,
                0d,
                structure.Confidence);
        }

        return null;
    }

    private MapRecognitionAttempt ValidateVpsgScaleCandidates(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapRecord map,
        string floorKey,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        LockedFloorFeatureFit fit,
        VpsgBootstrapResult bootstrap,
        bool usedVpsg,
        bool liveFrameCacheHit,
        double liveStructureExtractionMilliseconds,
        double bootstrapMilliseconds)
    {
        var candidates = (bootstrap.Mode == VpsgScaleMode.Structure
                || bootstrap.Mode == VpsgScaleMode.Both)
            && bootstrap.Structure is { } structure
            ? new[] { fit.Scale }
                .Concat(structure.ScaleCandidates
                    .Select(scale => scale * frame.PhysicalPixelsPerComputationPixel))
            : [fit.Scale];
        var distinctCandidates = candidates
            .Where(scale => double.IsFinite(scale) && scale > 0.05d)
            .DistinctBy(scale => Math.Round(scale, 9))
            .Take(2)
            .ToArray();
        var attempts = new List<(MapRecognitionAttempt Attempt, LockedFloorFeatureFit Fit, int Index)>();
        for (var index = 0; index < distinctCandidates.Length; index++)
        {
            var candidateFit = fit with { Scale = distinctCandidates[index] };
            var validationSeed = MapFeatureCacheRules.CreateScaleSeed(
                map,
                floorKey,
                candidateFit.Scale,
                frame.ViewportBounds.X + candidateFit.OffsetX,
                frame.ViewportBounds.Y + candidateFit.OffsetY);
            var attempt = AlignWithCachedScale(
                frame,
                selectedMapId,
                floorKey,
                validationSeed,
                alignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence,
                restrictTranslationToSeed: !usedVpsg);
            PopulateVpsgDiagnostics(
                attempt.Diagnostics,
                candidateFit,
                bootstrap,
                usedVpsg,
                index,
                distinctCandidates.Length,
                liveFrameCacheHit,
                liveStructureExtractionMilliseconds);
            attempt.Diagnostics.ScaleBootstrapValidated =
                usedVpsg && attempt.Recognition is not null;
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"VPSG2 scale basin validation · index={index + 1}/{distinctCandidates.Length} "
                + $"· scale={candidateFit.Scale:F5} "
                + $"· validation={(attempt.Recognition is null ? "rejected" : "accepted")}",
                elapsedMs: attempt.Diagnostics.TotalMilliseconds,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["candidateIndex"] = index,
                    ["candidateCount"] = distinctCandidates.Length,
                    ["scale"] = candidateFit.Scale,
                    ["accepted"] = attempt.Recognition is not null,
                    ["bestScore"] = attempt.StructureResult?.BestScore,
                    ["rejection"] = attempt.FailureReason
                });
            attempts.Add((attempt, candidateFit, index));
            if (attempt.Recognition is not null)
                break;
        }

        var selected = attempts
            .OrderBy(item => item.Attempt.Recognition is null ? 1 : 0)
            .ThenBy(item => item.Attempt.StructureResult?.BestScore
                ?? double.PositiveInfinity)
            .First();
        PopulateVpsgDiagnostics(
            selected.Attempt.Diagnostics,
            selected.Fit,
            bootstrap,
            usedVpsg,
            selected.Index,
            distinctCandidates.Length,
            liveFrameCacheHit,
            liveStructureExtractionMilliseconds);
        selected.Attempt.Diagnostics.ScaleBootstrapValidated =
            usedVpsg && selected.Attempt.Recognition is not null;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            selected.Attempt.Recognition is null
                ? MapLogLevel.Warning
                : MapLogLevel.Info,
            $"VPSG2 缩放预处理 · mode={bootstrap.Mode} "
            + $"· method={selected.Attempt.Diagnostics.ScaleBootstrapMethod} "
            + $"· map={map.SequenceNumber}#{floorKey} "
            + $"· scale={selected.Fit.Scale:F5} "
            + $"· cost={selected.Attempt.Diagnostics.ScaleBootstrapCost:F3} "
            + $"· margin={selected.Attempt.Diagnostics.ScaleBootstrapMargin:P1} "
            + $"· tested={selected.Attempt.Diagnostics.ScaleBootstrapTestedScaleCount} "
            + $"· basin={selected.Index + 1}/{distinctCandidates.Length} "
            + $"· hint={selected.Attempt.Diagnostics.ScaleBootstrapHintScale?.ToString("F5") ?? "none"} "
            + $"· hintConfidence={selected.Attempt.Diagnostics.ScaleBootstrapHintConfidence:P0} "
            + $"· validation={(selected.Attempt.Recognition is null ? "rejected" : "accepted")}",
            elapsedMs: bootstrapMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scale"] = selected.Fit.Scale,
                ["finalScale"] =
                    selected.Attempt.Recognition?.Result.OverlayTransform?.ScaleX,
                ["inliers"] = selected.Fit.InlierCount,
                ["residual"] = selected.Fit.Residual,
                ["featureConfidence"] = selected.Fit.Confidence,
                ["identityPriorConfidence"] = identityPriorConfidence,
                ["vpsg"] = usedVpsg,
                ["mode"] = bootstrap.Mode.ToString(),
                ["method"] = selected.Attempt.Diagnostics.ScaleBootstrapMethod,
                ["cost"] = selected.Attempt.Diagnostics.ScaleBootstrapCost,
                ["margin"] = selected.Attempt.Diagnostics.ScaleBootstrapMargin,
                ["testedScaleCount"] = selected.Attempt.Diagnostics.ScaleBootstrapTestedScaleCount,
                ["candidateCount"] = distinctCandidates.Length,
                ["candidateScales"] = string.Join(",", distinctCandidates.Select(scale => scale.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))),
                ["selectedCandidateIndex"] = selected.Index,
                ["hintScale"] = selected.Attempt.Diagnostics.ScaleBootstrapHintScale,
                ["hintConfidence"] = selected.Attempt.Diagnostics.ScaleBootstrapHintConfidence,
                ["searchMinimumScale"] = selected.Attempt.Diagnostics.ScaleBootstrapSearchMinimum,
                ["searchMaximumScale"] = selected.Attempt.Diagnostics.ScaleBootstrapSearchMaximum,
                ["legacyScale"] = selected.Attempt.Diagnostics.ScaleBootstrapLegacyScale,
                ["legacyConfidence"] = selected.Attempt.Diagnostics.ScaleBootstrapLegacyConfidence,
                ["legacyMilliseconds"] = selected.Attempt.Diagnostics.ScaleBootstrapLegacyMilliseconds,
                ["structureMilliseconds"] = selected.Attempt.Diagnostics.ScaleBootstrapStructureMilliseconds,
                ["liveFrameCacheHit"] = liveFrameCacheHit,
                ["liveStructureExtractionMs"] = liveStructureExtractionMilliseconds,
                ["structureValidationAccepted"] = selected.Attempt.Recognition is not null,
                ["structureValidationFailure"] = selected.Attempt.FailureReason
            });
        return selected.Attempt;
    }

    private static void PopulateVpsgDiagnostics(
        MapScanDiagnostics diagnostics,
        LockedFloorFeatureFit fit,
        VpsgBootstrapResult bootstrap,
        bool usedVpsg,
        int selectedCandidateIndex,
        int candidateCount,
        bool liveFrameCacheHit,
        double liveStructureExtractionMilliseconds)
    {
        diagnostics.ScaleBootstrapAttempted = true;
        diagnostics.ScaleBootstrapSucceeded = usedVpsg;
        diagnostics.ScaleBootstrapValidated =
            usedVpsg && diagnostics.StructureAccepted;
        diagnostics.ScaleBootstrapScale = fit.Scale;
        diagnostics.ScaleBootstrapConfidence = fit.Confidence;
        diagnostics.ScaleBootstrapMode = bootstrap.Mode.ToString();
        diagnostics.ScaleBootstrapMethod = bootstrap.Structure is not null
            && bootstrap.Mode != VpsgScaleMode.LegacyAkaze
            ? "structure"
            : bootstrap.Legacy is not null ? "legacy-akaze" : "none";
        diagnostics.ScaleBootstrapCost = bootstrap.Structure?.Cost
            ?? bootstrap.Legacy?.Evidence.ResidualPixels
            ?? 0d;
        diagnostics.ScaleBootstrapMargin = bootstrap.Structure?.Margin ?? 0d;
        diagnostics.ScaleBootstrapTestedScaleCount =
            bootstrap.Structure?.TestedScaleCount ?? 0;
        diagnostics.ScaleBootstrapHintScale = bootstrap.Structure?.HintScale;
        diagnostics.ScaleBootstrapHintConfidence =
            bootstrap.Structure?.HintConfidence ?? 0d;
        diagnostics.ScaleBootstrapSearchMinimum =
            bootstrap.Structure?.SearchMinimumScale ?? 0d;
        diagnostics.ScaleBootstrapSearchMaximum =
            bootstrap.Structure?.SearchMaximumScale ?? 0d;
        diagnostics.ScaleBootstrapLegacyScale = bootstrap.Legacy is { } legacy
            ? legacy.Scale
            : null;
        diagnostics.ScaleBootstrapLegacyConfidence =
            bootstrap.Legacy?.Confidence ?? 0d;
        diagnostics.ScaleBootstrapLegacyMilliseconds = bootstrap.LegacyMilliseconds;
        diagnostics.ScaleBootstrapStructureMilliseconds = bootstrap.StructureMilliseconds;
        diagnostics.ScaleBootstrapCandidateCount = candidateCount;
        diagnostics.ScaleBootstrapSelectedCandidateIndex = selectedCandidateIndex;
        diagnostics.LiveStructurePreprocessMilliseconds +=
            liveStructureExtractionMilliseconds;
        diagnostics.StructurePreprocessMilliseconds +=
            liveStructureExtractionMilliseconds;
        _ = liveFrameCacheHit;
    }
}
