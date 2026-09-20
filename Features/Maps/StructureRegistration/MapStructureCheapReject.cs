using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static class MapStructureCheapReject
{
    private const int DownsampleFactor = 4;

    internal static bool TryReject(
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        out double elapsedMilliseconds,
        out string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        reason = string.Empty;
        try
        {
            var scale = request.LockedTransform.ScaleX;
            if (!double.IsFinite(scale) || scale <= 0d)
            {
                reason = "cheap-reject: invalid seed scale";
                return true;
            }

            using var query = MapStructureScaleSearch.CreateQuery(
                live,
                request.LiveRoi.Size(),
                scale);
            if (query.EdgeCount == 0
                || query.Bounds.Width <= 0
                || query.Bounds.Height <= 0)
            {
                reason = "cheap-reject: no live structure edges";
                return true;
            }

            if (query.Bounds.Width > reference.Edges.Width
                || query.Bounds.Height > reference.Edges.Height)
            {
                reason = "cheap-reject: query larger than reference";
                return true;
            }

            var expected = MapStructureScaleSearch.ExpectedReferenceLocation(
                request,
                scale,
                query.Bounds);
            var referenceWidth = Math.Max(
                1,
                reference.Edges.Width / DownsampleFactor);
            var referenceHeight = Math.Max(
                1,
                reference.Edges.Height / DownsampleFactor);
            var queryWidth = Math.Max(
                1,
                query.Bounds.Width / DownsampleFactor);
            var queryHeight = Math.Max(
                1,
                query.Bounds.Height / DownsampleFactor);
            if (queryWidth > referenceWidth || queryHeight > referenceHeight)
            {
                reason = "cheap-reject: downsampled query larger than reference";
                return true;
            }

            using var referenceBinary = new Mat();
            using var queryEdges = new Mat(query.Edges, query.Bounds);
            using var queryBinary = new Mat();
            Cv2.Threshold(
                reference.Edges,
                referenceBinary,
                0d,
                255d,
                ThresholdTypes.Binary);
            Cv2.Threshold(
                queryEdges,
                queryBinary,
                0d,
                255d,
                ThresholdTypes.Binary);
            using var edgeKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(3, 3));
            using var referenceDilated = new Mat();
            using var queryDilated = new Mat();
            Cv2.Dilate(referenceBinary, referenceDilated, edgeKernel);
            Cv2.Dilate(queryBinary, queryDilated, edgeKernel);
            using var referenceEdges = new Mat();
            using var querySmall = new Mat();
            Cv2.Resize(
                referenceDilated,
                referenceEdges,
                new Size(referenceWidth, referenceHeight),
                interpolation: InterpolationFlags.Nearest);
            Cv2.Resize(
                queryDilated,
                querySmall,
                new Size(queryWidth, queryHeight),
                interpolation: InterpolationFlags.Nearest);

            var searchRadius = Math.Max(
                DownsampleFactor,
                (int)Math.Ceiling(
                    Math.Max(8d, request.Tuning.PreviousAlignmentSearchRadiusPixels)
                    / Math.Max(0.0001d, scale)
                    / DownsampleFactor));
            var expectedX = (int)Math.Round(
                expected.X / (double)DownsampleFactor);
            var expectedY = (int)Math.Round(
                expected.Y / (double)DownsampleFactor);
            var left = Math.Clamp(
                expectedX - searchRadius,
                0,
                referenceWidth - queryWidth);
            var top = Math.Clamp(
                expectedY - searchRadius,
                0,
                referenceHeight - queryHeight);
            var window = new Rect(
                left,
                top,
                Math.Min(referenceWidth - left, queryWidth + searchRadius * 2),
                Math.Min(referenceHeight - top, queryHeight + searchRadius * 2));
            if (window.Width < queryWidth || window.Height < queryHeight)
            {
                reason = "cheap-reject: seed outside reference bounds";
                return true;
            }

            using var referenceWindow = new Mat(referenceEdges, window);
            using var response = new Mat();
            Cv2.MatchTemplate(
                referenceWindow,
                querySmall,
                response,
                TemplateMatchModes.CCorrNormed);
            Cv2.MinMaxLoc(
                response,
                out _,
                out _,
                out _,
                out var bestLocation);
            var patch = new Rect(
                window.X + bestLocation.X,
                window.Y + bestLocation.Y,
                queryWidth,
                queryHeight);
            using var referencePatch = new Mat(referenceEdges, patch);
            using var inverse = new Mat();
            using var distance = new Mat();
            using var covered = new Mat();
            using var withinTolerance = new Mat();
            Cv2.BitwiseNot(referencePatch, inverse);
            Cv2.DistanceTransform(
                inverse,
                distance,
                DistanceTypes.L2,
                DistanceTransformMasks.Mask3);
            var edgeCount = Math.Max(1, Cv2.CountNonZero(querySmall));
            var chamfer = Cv2.Mean(distance, querySmall).Val0
                * DownsampleFactor;
            var tolerance = Math.Max(
                1d,
                request.Tuning.EdgeDistanceTolerancePixels
                    / DownsampleFactor);
            Cv2.Compare(
                distance,
                tolerance,
                withinTolerance,
                CmpTypes.LE);
            Cv2.BitwiseAnd(withinTolerance, querySmall, covered);
            var coverage = Cv2.CountNonZero(covered) / (double)edgeCount;
            // This is a reject-only gate. Keep it materially looser than the
            // physical 3px acceptance gate so quarter-scale quantization can
            // never reject a candidate that formal validation could explain.
            var maximumChamfer = request.Tuning.MaximumChamferPixels * 4d;
            var minimumCoverage = request.Tuning.MinimumEdgeCoverage * 0.25d;
            if (chamfer > maximumChamfer || coverage < minimumCoverage)
            {
                reason = $"cheap-reject: chamfer={chamfer:F2}px, "
                    + $"coverage={coverage:P0}, "
                    + $"limits={maximumChamfer:F2}px/{minimumCoverage:P0}";
                return true;
            }

            return false;
        }
        finally
        {
            stopwatch.Stop();
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
