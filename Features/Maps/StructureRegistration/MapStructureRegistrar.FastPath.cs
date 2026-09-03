using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private MapStructureRegistrationResult RegisterInternal(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning)
    {
        const bool canUseFast = true;

        // Low structure has an explicit bounded plan. Do not let the generic
        // fast/legacy fallback chain reintroduce an unbounded scale sweep.
        if (request.Channel == MapAlignmentChannel.LowStructure)
            return RegisterLegacy(request);

        if (tuning.FastAlignmentShadowMode && canUseFast)
        {
            var legacyResult = RegisterLegacy(request);
            MapStructureRegistrationResult shadowFast;
            using (var fastShadow = MapOperationTraceAmbient.StartChild(
                       "fast_coarse_shadow",
                       MapOperationWaitKind.Compute))
            {
                shadowFast = TryFastCoarseAlign(request);
            }
            try
            {
                var ft = shadowFast.Transform;
                var lt = legacyResult.Transform;
                var td = 0d; var sd = 0d;
                if (ft is not null && lt is not null)
                {
                    td = Math.Sqrt(Math.Pow(ft.OffsetX - lt.OffsetX, 2d)
                        + Math.Pow(ft.OffsetY - lt.OffsetY, 2d));
                    sd = Math.Abs(ft.ScaleX - lt.ScaleX);
                }
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"Shadow对比 · Fast={(shadowFast.Accepted ? "通过" : "未通过")} "
                    + $"Legacy={(legacyResult.Accepted ? "通过" : "未通过")} "
                    + $"Δ={td:F1}px Δs={sd:F4}",
                    details: new()
                    {
                        ["fastAccepted"] = shadowFast.Accepted,
                        ["legacyAccepted"] = legacyResult.Accepted,
                        ["transformDeltaPx"] = td, ["scaleDelta"] = sd,
                        ["fastTotalMs"] = shadowFast.SearchMilliseconds + shadowFast.RefineMilliseconds,
                        ["legacyTotalMs"] = legacyResult.SearchMilliseconds + legacyResult.RefineMilliseconds,
                        ["fastRejection"] = shadowFast.RejectionReason.ToString(),
                        ["legacyRejection"] = legacyResult.RejectionReason.ToString(),
                    });
            }
            catch { }
            return legacyResult;
        }

        if (tuning.EnableFastAlignment && canUseFast)
        {
            try
            {
                MapStructureRegistrationResult fastResult;
                using (var fastSearch = MapOperationTraceAmbient.StartChild(
                           "fast_coarse_search",
                           MapOperationWaitKind.Compute))
                {
                    fastResult = TryFastCoarseAlign(request);
                }
                var fallbackToLegacy = tuning.Mode ==
                    MapStructureRegistrationMode.ScanVerification
                        ? ShouldRunScanLegacyFallback(fastResult)
                        : tuning.FastFallbackToLegacy;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    fastResult.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
                    fastResult.Accepted
                        ? "快速粗搜索通过"
                        : fallbackToLegacy
                            ? "快速粗搜索未早停，将进入完整搜索"
                            : "快速粗搜索未通过",
                    elapsedMs: fastResult.PreprocessMilliseconds
                        + fastResult.SearchMilliseconds
                        + fastResult.RefineMilliseconds,
                    details: new()
                    {
                        ["usedFastStrategy"] = true, ["accepted"] = fastResult.Accepted,
                        ["fallbackToLegacy"] =
                            !fastResult.Accepted && fallbackToLegacy,
                        ["preprocessMs"] = fastResult.PreprocessMilliseconds,
                        ["fastCoarseMs"] = fastResult.FastCoarseSearchMilliseconds,
                        ["fastCandidates"] = fastResult.FastCoarseCandidateCount,
                        ["rejection"] = fastResult.RejectionReason.ToString(),
                        ["fallbackReasonAllowed"] =
                            ShouldRunScanLegacyFallback(fastResult),
                        ["lockedScale"] = fastResult.LockedScale,
                        ["referenceWidth"] = fastResult.ReferenceWidth,
                        ["referenceHeight"] = fastResult.ReferenceHeight,
                        ["queryEdgePixels"] = fastResult.QueryEdgePixels,
                        ["queryBoundsX"] = fastResult.QueryBoundsX,
                        ["queryBoundsY"] = fastResult.QueryBoundsY,
                        ["queryBoundsWidth"] = fastResult.QueryBoundsWidth,
                        ["queryBoundsHeight"] = fastResult.QueryBoundsHeight
                    });
                if (fastResult.Accepted) return fastResult;
                if (!fallbackToLegacy) return fastResult;
            }
            catch (Exception ex)
            {
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Error,
                    tuning.FastFallbackToLegacy
                        && tuning.Mode != MapStructureRegistrationMode.ScanVerification
                        ? $"快速粗搜索异常，回退 Legacy：{ex.Message}"
                        : $"快速粗搜索异常，固定路径结束：{ex.Message}");
                if (!tuning.FastFallbackToLegacy
                    || tuning.Mode == MapStructureRegistrationMode.ScanVerification)
                {
                    return MapStructureValidator.BuildResult(
                        MapStructureRejectionReason.NoCandidate,
                        usedFastStrategy: true,
                        scaleHypothesisCount: 1,
                        usedRestrictedSearch: request.RestrictSearchToLockedTransform);
                }
            }
        }

        return RegisterLegacy(request);
    }

    internal static bool ShouldRunScanLegacyFallback(
        MapStructureRegistrationResult fast) =>
        fast.RejectionReason is
            MapStructureRejectionReason.NoCandidate
            or MapStructureRejectionReason.AmbiguousCandidates
            or MapStructureRejectionReason.WeakAbsoluteScore
            or MapStructureRejectionReason.RefinementFailed;
}
