using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureRefiner
{
    internal static MapStructureCandidate RefineCandidate(
        MapStructureCandidate candidate,
        MapStructureFeatures live,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale)
    {
        if (candidate.CompositeCost <= StructureRegistrationRules.RefinementEarlyExitScore)
            return candidate;

        // 互逆缩放：candidate 的 ReferenceX/Y 和 Scale 已被 Evaluate
        // 映射到原始参考图空间。RefineCandidate 需要在匹配空间
        // （referenceDistance 所在坐标空间）中操作，避免坐标双重转换。
        var refScale = reciprocalScale.ReferenceScale;
        var matchScale = candidate.Scale / refScale;
        var refWidth = reciprocalScale.StructureMask?.Width ?? reference.Edges.Width;
        var refHeight = reciprocalScale.StructureMask?.Height ?? reference.Edges.Height;

        using var query = MapStructureScaleSearch.CreateQuery(live, request.LiveRoi.Size(), matchScale);
        var best = candidate;
        // Translation-only coarse-to-fine refinement. The search never
        // introduces scale, rotation, affine, or perspective freedom.
        foreach (var step in StructureRegistrationRules.RefinementSteps)
        {
            // 每轮以当前最佳坐标为中心（映射回匹配空间）
            var centerX = (int)Math.Round(best.ReferenceX * refScale);
            var centerY = (int)Math.Round(best.ReferenceY * refScale);
            foreach (var (deltaX, deltaY) in new[]
                     {
                         (-step, -step), (0, -step), (step, -step),
                         (-step, 0), (step, 0),
                         (-step, step), (0, step), (step, step)
                     })
            {
                var x = centerX + deltaX;
                var y = centerY + deltaY;
                if (x < 0
                    || y < 0
                    || x + query.Bounds.Width >= refWidth
                    || y + query.Bounds.Height >= refHeight)
                {
                    continue;
                }
                var evaluated = MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    matchScale,
                    x,
                    y,
                    candidate.UsedGlobalSearch,
                    tuning,
                    reciprocalScale) with
                {
                    FeatureInlierCount = candidate.FeatureInlierCount,
                    FeatureConsensus = candidate.FeatureConsensus
                };
                if (evaluated.CompositeCost < best.CompositeCost)
                    best = evaluated;
            }
        }
        if (!tuning.EnableEccRefinement)
            return best;
        if (best.CompositeCost <= tuning.SkipEccScoreThreshold)
            return best;
        return RefineTranslationWithEcc(best, query, reference);
    }

    internal static bool CanSkipLocalRefinement(
        IReadOnlyList<MapStructureCandidate> ranked,
        MapStructureRegistrationTuning tuning,
        bool restrictedSearch = false)
    {
        if (tuning.EnableEccRefinement || ranked.Count == 0)
            return false;
        var best = ranked[0];
        var secondScore = ranked.Count > 1
            ? ranked[1].CompositeCost
            : double.PositiveInfinity;
        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                / Math.Max(StructureRegistrationRules.MarginNormalizationFloor, secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch ? StructureRegistrationRules.GlobalSearchMarginMultiplier : 1d);
        var chamferLimit = restrictedSearch
            ? Math.Min(
                tuning.MaximumChamferPixels,
                tuning.RestrictedSearchMaximumChamferPixels)
            : tuning.MaximumChamferPixels;
        return MapStructureValidator.Validate(
                    best, margin, requiredMargin, tuning, restrictedSearch)
                == MapStructureRejectionReason.None
            && best.ChamferPixels
                <= chamferLimit
                    * StructureRegistrationRules.StrictChamferFactor
            && best.EdgeCoverage
                >= tuning.MinimumEdgeCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.OccupancyCoverage
                >= tuning.MinimumOccupancyCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.ConsistentPartitions >= Math.Max(
                StructureRegistrationRules.CanSkipRefinementMinPartitions,
                tuning.MinimumConsistentPartitions)
            && margin >= Math.Max(
                StructureRegistrationRules.MinimumReplacementMargin,
                requiredMargin * 2d);
    }

    internal static MapStructureCandidate RefineTranslationWithEcc(
        MapStructureCandidate candidate,
        QueryGeometry query,
        MapStructureFeatures reference)
    {
        if (candidate.ReferenceX < 0
            || candidate.ReferenceY < 0
            || candidate.ReferenceX + query.Bounds.Width > reference.Edges.Width
            || candidate.ReferenceY + query.Bounds.Height > reference.Edges.Height)
        {
            return candidate;
        }
        try
        {
            using var referencePatch = new Mat(
                reference.Edges,
                new Rect(
                    candidate.ReferenceX,
                    candidate.ReferenceY,
                    query.Bounds.Width,
                    query.Bounds.Height));
            using var queryPatch = new Mat(query.Edges, query.Bounds);
            using var mask = new Mat(query.Structure, query.Bounds);
            using var referenceFloat = new Mat();
            using var queryFloat = new Mat();
            referencePatch.ConvertTo(referenceFloat, MatType.CV_32FC1, 1d / 255d);
            queryPatch.ConvertTo(queryFloat, MatType.CV_32FC1, 1d / 255d);
            using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
            var correlation = Cv2.FindTransformECC(
                referenceFloat,
                queryFloat,
                warp,
                MotionTypes.Translation,
                new TermCriteria(
                    CriteriaTypes.Count | CriteriaTypes.Eps,
                    StructureRegistrationRules.EccMaxIterations,
                    StructureRegistrationRules.EccEpsilon),
                mask,
                StructureRegistrationRules.EccGaussFocalLen);
            var translationX = warp.At<float>(0, 2);
            var translationY = warp.At<float>(1, 2);
            if (!double.IsFinite(correlation)
                || correlation < StructureRegistrationRules.EccMinCorrelation
                || !float.IsFinite(translationX)
                || !float.IsFinite(translationY)
                || Math.Abs(translationX) > StructureRegistrationRules.EccMaxTranslationShift
                || Math.Abs(translationY) > StructureRegistrationRules.EccMaxTranslationShift)
            {
                return candidate;
            }

            // findTransformECC returns the warp from template coordinates to
            // input coordinates (OpenCV aligns with WARP_INVERSE_MAP). For
            // screen = scale * reference + offset, that warp translation is
            // added to the screen offset.
            return candidate with
            {
                OffsetX = candidate.OffsetX
                    + (translationX * candidate.Scale),
                OffsetY = candidate.OffsetY
                    + (translationY * candidate.Scale),
                EccConverged = true,
                EccCorrelation = correlation
            };
        }
        catch (OpenCVException)
        {
            return candidate;
        }
    }
}
