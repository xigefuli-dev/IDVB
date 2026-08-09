using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructurePyramidSearch
{
    internal static void CollectPyramidCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        if (reference.EdgePyramid.Count < StructureRegistrationRules.PyramidMinLevels)
            return;
        using var fullTemplate = new Mat(query.Edges, query.Bounds);
        var targetWidth = Math.Max(1, fullTemplate.Width / StructureRegistrationRules.PyramidDownsampleFactor);
        var targetHeight = Math.Max(1, fullTemplate.Height / StructureRegistrationRules.PyramidDownsampleFactor);
        if (targetWidth >= reference.EdgePyramid[2].Width
            || targetHeight >= reference.EdgePyramid[2].Height)
        {
            return;
        }

        using var template = new Mat();
        Cv2.Resize(
            fullTemplate,
            template,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Area);
        using var inverse = new Mat();
        using var distanceMap = new Mat();
        Cv2.BitwiseNot(reference.EdgePyramid[2], inverse);
        Cv2.DistanceTransform(
            inverse,
            distanceMap,
            DistanceTypes.L2,
            DistanceTransformMasks.Mask3);
        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var scores = new Mat();
        Cv2.MatchTemplate(
            distanceMap,
            templateFloat,
            scores,
            TemplateMatchModes.CCorr);
        Cv2.Multiply(
            scores,
            1d / Math.Max(1, Cv2.CountNonZero(template)),
            scores);
        var suppression = Math.Max(
            3,
            Math.Min(template.Width, template.Height) / 3);
        for (var index = 0;
             index < tuning.MaximumTranslationCandidates;
             index++)
        {
            Cv2.MinMaxLoc(
                scores,
                out var minimum,
                out _,
                out var location,
                out _);
            if (!double.IsFinite(minimum))
                break;
            var referenceX = location.X * StructureRegistrationRules.PyramidDownsampleFactor;
            var referenceY = location.Y * StructureRegistrationRules.PyramidDownsampleFactor;
            if (referenceX + query.Bounds.Width < reference.Edges.Width
                && referenceY + query.Bounds.Height < reference.Edges.Height)
            {
                output.Add(MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch: true,
                    tuning,
                    reciprocalScale));
            }
            var left = Math.Max(0, location.X - suppression);
            var top = Math.Max(0, location.Y - suppression);
            var right = Math.Min(
                scores.Width,
                location.X + suppression + 1);
            var bottom = Math.Min(
                scores.Height,
                location.Y + suppression + 1);
            Cv2.Rectangle(
                scores,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity),
                -1);
        }
    }
}
