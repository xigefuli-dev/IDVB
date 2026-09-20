using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production-grade fast native structure line extractor for live game viewport frames (VPSG 3.0 Phase 2.1).
/// Fully migrates the Phase 0.5 A-4 ablation winner (Cheap Dynamic Exclusion + Pure Morphology)
/// while generating a semantically correct ValidMask with steady-state scratch buffer reuse.
/// </summary>
public static class Vpsg3FastLiveExtractor
{
    private const double ContourMinPerimeter = 30d;
    private const double HoleMinArea = 900d;
    private const double ApproxEpsilon = 0.55d;

    [ThreadStatic]
    private static Vpsg3LiveExtractorScratch? t_defaultScratch;

    /// <summary>
    /// Extracts live structural edges and valid observation mask from a query game frame or cropped viewport.
    /// </summary>
    /// <param name="source">Source frame (BGRA, BGR, or Grayscale).</param>
    /// <param name="viewportBounds">Screen coordinate bounds of the source image, if known.</param>
    /// <param name="maxSparsePoints">Maximum number of sparse edge points to sample on-demand for downstream verification.</param>
    /// <param name="scratch">Optional session/worker-owned scratch buffers to eliminate intermediate Mat allocations.</param>
    public static Vpsg3LiveObservation Extract(
        Mat source,
        MapScreenRect? viewportBounds = null,
        int maxSparsePoints = 150,
        Vpsg3LiveExtractorScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("Source frame cannot be empty.", nameof(source));

        var sw = Stopwatch.StartNew();

        var width = source.Width;
        var height = source.Height;
        var size = source.Size();
        var bounds = viewportBounds ?? new MapScreenRect(0, 0, width, height);

        var s = scratch ?? (t_defaultScratch ??= new Vpsg3LiveExtractorScratch());
        s.PrepareForSize(size);

        // 1. Channel normalization
        var bgr = NormalizeToBgr(source, s);

        // 2. Color space conversion to HSV for structure semantics
        Vpsg3LiveExtractorScratch.EnsureMatSized(s.Hsv, size, MatType.CV_8UC3);
        Cv2.CvtColor(bgr, s.Hsv, ColorConversionCodes.BGR2HSV);

        // 3. Fast dynamic exclusion (HUD and top glyph exclusion without connected components)
        ComputeFastDynamicExclusion(s.Hsv, s);

        // 4. Classify room and corridor semantic areas with pure morphological filtering
        ClassifyStructurePureMorphology(s);

        // 5. Semantic candidate edge extraction via contour approximation
        ExtractSemanticCandidateEdges(s);

        // 6. Strong edge support via Canny on grayscale + dilation
        ComputeStrongSourceEdgeSupport(bgr, s);

        // 7. Observed edges: semantic candidates supported by strong photometric edges
        var observedEdges = new Mat(size, MatType.CV_8UC1);
        Cv2.BitwiseAnd(s.CandidateEdges, s.Support, observedEdges);

        // 8. Valid mask generation (fog frontier + exclusion masking)
        // Areas with semantic edges but missing strong photometric support represent fog frontiers
        Cv2.BitwiseNot(s.Support, s.NotSupport);
        Cv2.BitwiseAnd(s.CandidateEdges, s.NotSupport, s.UncertainFrontier);

        // In-place dilation on uncertain frontier
        Cv2.Dilate(s.UncertainFrontier, s.UncertainFrontier, s.K11);

        // Exclusion dilation
        Cv2.Dilate(s.Exclusion, s.DilatedExclusion, s.K5);

        // Combined invalid mask
        Cv2.BitwiseOr(s.UncertainFrontier, s.DilatedExclusion, s.Invalid);

        // Final valid mask (inverted invalid, with observed edges forced valid)
        var validMask = new Mat(size, MatType.CV_8UC1);
        Cv2.BitwiseNot(s.Invalid, validMask);
        validMask.SetTo(Scalar.White, observedEdges);

        // 9. Edge pixel metrics
        var edgePixelCount = Cv2.CountNonZero(observedEdges);
        var validPixelCount = Cv2.CountNonZero(validMask);

        sw.Stop();

        return new Vpsg3LiveObservation(
            observedEdges: observedEdges,
            validMask: validMask,
            width: width,
            height: height,
            edgePixelCount: edgePixelCount,
            validStructurePixelCount: validPixelCount,
            viewportBounds: bounds,
            maxSparsePoints: maxSparsePoints,
            sparseEdgePoints: null,
            extractionMilliseconds: sw.Elapsed.TotalMilliseconds);
    }

    private static Mat NormalizeToBgr(Mat source, Vpsg3LiveExtractorScratch scratch)
    {
        switch (source.Channels())
        {
            case 3:
                return source;
            case 4:
                Vpsg3LiveExtractorScratch.EnsureMatSized(scratch.Bgr, source.Size(), MatType.CV_8UC3);
                Cv2.CvtColor(source, scratch.Bgr, ColorConversionCodes.BGRA2BGR);
                return scratch.Bgr;
            case 1:
                Vpsg3LiveExtractorScratch.EnsureMatSized(scratch.Bgr, source.Size(), MatType.CV_8UC3);
                Cv2.CvtColor(source, scratch.Bgr, ColorConversionCodes.GRAY2BGR);
                return scratch.Bgr;
            default:
                throw new InvalidDataException($"Unsupported image channel count: {source.Channels()}");
        }
    }

    private static void ComputeFastDynamicExclusion(Mat hsv, Vpsg3LiveExtractorScratch scratch)
    {
        var h = hsv.Height;
        var w = hsv.Width;

        // 1. Bottom-left green avatar HUD detection
        var greenRoiY = (int)(0.68 * h);
        var greenRoiW = (int)(0.28 * w);
        var greenRoiH = h - greenRoiY;
        if (greenRoiW > 0 && greenRoiH > 0)
        {
            var greenRect = new Rect(0, greenRoiY, greenRoiW, greenRoiH);
            using var hsvGreen = new Mat(hsv, greenRect);
            Cv2.InRange(hsvGreen, new Scalar(35, 55, 45), new Scalar(95, 255, 255), scratch.GreenSeed);

            if (Cv2.CountNonZero(scratch.GreenSeed) > 40)
            {
                var fillRect = new Rect(0, (int)(0.72 * h), (int)(0.24 * w), (int)(0.28 * h));
                scratch.Exclusion[fillRect].SetTo(Scalar.White);
            }
        }

        // 2. Top white glyph HUD detection
        var topRoiY = (int)(0.03 * h);
        var topRoiH = (int)(0.12 * h);
        var topRoiX = (int)(0.10 * w);
        var topRoiW = (int)(0.70 * w);
        if (topRoiW > 0 && topRoiH > 0)
        {
            var topRect = new Rect(topRoiX, topRoiY, topRoiW, topRoiH);
            using var hsvTop = new Mat(hsv, topRect);
            Cv2.InRange(hsvTop, new Scalar(0, 0, 120), new Scalar(180, 60, 255), scratch.WhiteSeed);

            if (Cv2.CountNonZero(scratch.WhiteSeed) > 100)
            {
                scratch.Exclusion[topRect].SetTo(Scalar.White);
            }
        }
    }

    private static void ClassifyStructurePureMorphology(Vpsg3LiveExtractorScratch s)
    {
        Cv2.InRange(s.Hsv, new Scalar(0, 18, 82), new Scalar(25, 165, 200), s.Room1);
        Cv2.InRange(s.Hsv, new Scalar(170, 18, 82), new Scalar(179, 165, 200), s.Room2);
        Cv2.BitwiseOr(s.Room1, s.Room2, s.Room);

        Cv2.InRange(s.Hsv, new Scalar(95, 14, 82), new Scalar(130, 105, 200), s.Corridor);

        s.Room.SetTo(Scalar.Black, s.Exclusion);
        s.Corridor.SetTo(Scalar.Black, s.Exclusion);

        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Close, s.K3);

        Cv2.MorphologyEx(s.Corridor, s.Corridor, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(s.Corridor, s.Corridor, MorphTypes.Close, s.K3);
    }

    private static void ExtractSemanticCandidateEdges(Vpsg3LiveExtractorScratch s)
    {
        AppendApproximatedContours(s.Room, s.ApproxContourBatch);
        AppendApproximatedContours(s.Corridor, s.ApproxContourBatch);

        if (s.ApproxContourBatch.Count > 0)
        {
            Cv2.DrawContours(
                s.CandidateEdges,
                s.ApproxContourBatch,
                contourIdx: -1,
                color: Scalar.White,
                thickness: 2,
                lineType: LineTypes.Link8);
        }
    }

    private static void AppendApproximatedContours(Mat mask, List<Point[]> batch)
    {
        Cv2.FindContours(
            mask,
            out var contours,
            out var hierarchy,
            RetrievalModes.CComp,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0 || hierarchy.Length == 0)
            return;

        for (var idx = 0; idx < contours.Length; idx++)
        {
            var contour = contours[idx];
            var perimeter = Cv2.ArcLength(contour, closed: true);
            if (perimeter < ContourMinPerimeter)
                continue;

            var parent = hierarchy[idx].Parent;
            if (parent != -1 && Math.Abs(Cv2.ContourArea(contour)) < HoleMinArea)
                continue;

            var approx = Cv2.ApproxPolyDP(contour, ApproxEpsilon, closed: true);
            batch.Add(approx);
        }
    }

    private static void ComputeStrongSourceEdgeSupport(Mat bgr, Vpsg3LiveExtractorScratch s)
    {
        Cv2.CvtColor(bgr, s.Gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Canny(s.Gray, s.CannyStrong, 80d, 180d, apertureSize: 3, L2gradient: true);
        Cv2.Dilate(s.CannyStrong, s.Support, s.K5);
    }
}
