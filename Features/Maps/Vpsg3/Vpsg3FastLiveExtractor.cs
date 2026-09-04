using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production-grade fast native structure line extractor for live game viewport frames (VPSG 3.0 Phase 2).
/// Fully migrates the Phase 0.5 A-4 ablation winner (Cheap Dynamic Exclusion + Pure Morphology)
/// while generating a semantically correct ValidMask and pre-sampled sparse verification points.
/// </summary>
public static class Vpsg3FastLiveExtractor
{
    private const double ContourMinPerimeter = 30d;
    private const double HoleMinArea = 900d;
    private const double ApproxEpsilon = 0.55d;

    /// <summary>
    /// Extracts live structural edges and valid observation mask from a query game frame or cropped viewport.
    /// </summary>
    /// <param name="source">Source frame (BGRA, BGR, or Grayscale).</param>
    /// <param name="viewportBounds">Screen coordinate bounds of the source image, if known.</param>
    /// <param name="maxSparsePoints">Maximum number of sparse edge points to sample for downstream verification.</param>
    public static Vpsg3LiveObservation Extract(
        Mat source,
        MapScreenRect? viewportBounds = null,
        int maxSparsePoints = 150)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("Source frame cannot be empty.", nameof(source));

        var sw = Stopwatch.StartNew();

        var width = source.Width;
        var height = source.Height;
        var bounds = viewportBounds ?? new MapScreenRect(0, 0, width, height);

        // 1. Channel normalization
        using var bgr = EnsureBgr(source);

        // 2. Color space conversion to HSV for structure semantics
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        // 3. Fast dynamic exclusion (HUD and top glyph exclusion without connected components)
        using var exclusion = FastDynamicExclusion(bgr, hsv);

        // 4. Classify room and corridor semantic areas with pure morphological filtering
        var (room, corridor) = ClassifyStructurePureMorphology(hsv, exclusion);
        using (room)
        using (corridor)
        {
            // 5. Semantic candidate edge extraction via contour approximation
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            // 6. Strong edge support via Canny on grayscale + dilation
            using var support = StrongSourceEdgeSupport(bgr);

            // 7. Observed edges: semantic candidates supported by strong photometric edges
            var observedEdges = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, observedEdges);

            // 8. Valid mask generation (fog frontier + exclusion masking)
            // Areas with semantic edges but missing strong photometric support represent fog frontiers
            using var notSupport = new Mat();
            Cv2.BitwiseNot(support, notSupport);

            using var uncertainFrontier = new Mat();
            Cv2.BitwiseAnd(candidateEdges, notSupport, uncertainFrontier);

            using var frontierKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11));
            using var overlayKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));

            using var dilatedFrontier = new Mat();
            Cv2.Dilate(uncertainFrontier, dilatedFrontier, frontierKernel);

            using var dilatedExclusion = new Mat();
            Cv2.Dilate(exclusion, dilatedExclusion, overlayKernel);

            using var invalid = new Mat();
            Cv2.BitwiseOr(dilatedFrontier, dilatedExclusion, invalid);

            var validMask = new Mat();
            Cv2.BitwiseNot(invalid, validMask);
            validMask.SetTo(Scalar.White, observedEdges);

            // 9. Edge pixel metrics
            var edgePixelCount = Cv2.CountNonZero(observedEdges);
            var validPixelCount = Cv2.CountNonZero(validMask);

            // 10. Sample sparse edge points for downstream V-A bit-testing
            var sparsePoints = SampleSparseEdgePoints(observedEdges, maxSparsePoints);

            sw.Stop();

            return new Vpsg3LiveObservation(
                observedEdges: observedEdges,
                validMask: validMask,
                width: width,
                height: height,
                edgePixelCount: edgePixelCount,
                validStructurePixelCount: validPixelCount,
                viewportBounds: bounds,
                sparseEdgePoints: sparsePoints,
                extractionMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
    }

    private static Mat EnsureBgr(Mat source)
    {
        var bgr = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                source.CopyTo(bgr);
                break;
            case 1:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
            default:
                throw new InvalidDataException($"Unsupported image channel count: {source.Channels()}");
        }
        return bgr;
    }

    private static Mat FastDynamicExclusion(Mat bgr, Mat hsv)
    {
        var h = bgr.Height;
        var w = bgr.Width;
        var exclusion = Mat.Zeros(bgr.Size(), MatType.CV_8UC1).ToMat();

        // 1. Bottom-left green avatar HUD detection
        var greenRoiY = (int)(0.68 * h);
        var greenRoiW = (int)(0.28 * w);
        var greenRoiH = h - greenRoiY;
        if (greenRoiW > 0 && greenRoiH > 0)
        {
            var greenRect = new Rect(0, greenRoiY, greenRoiW, greenRoiH);
            using var hsvGreen = new Mat(hsv, greenRect);
            using var greenSeed = new Mat();
            Cv2.InRange(hsvGreen, new Scalar(35, 55, 45), new Scalar(95, 255, 255), greenSeed);

            var greenCount = Cv2.CountNonZero(greenSeed);
            if (greenCount > 40)
            {
                var fillRect = new Rect(0, (int)(0.72 * h), (int)(0.24 * w), (int)(0.28 * h));
                exclusion[fillRect].SetTo(Scalar.White);
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
            using var whiteSeed = new Mat();
            Cv2.InRange(hsvTop, new Scalar(0, 0, 120), new Scalar(180, 60, 255), whiteSeed);

            if (Cv2.CountNonZero(whiteSeed) > 100)
            {
                exclusion[topRect].SetTo(Scalar.White);
            }
        }

        return exclusion;
    }

    private static (Mat Room, Mat Corridor) ClassifyStructurePureMorphology(Mat hsv, Mat exclusion)
    {
        var room = new Mat();
        using var room1 = new Mat();
        using var room2 = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 18, 82), new Scalar(25, 165, 200), room1);
        Cv2.InRange(hsv, new Scalar(170, 18, 82), new Scalar(179, 165, 200), room2);
        Cv2.BitwiseOr(room1, room2, room);

        var corridor = new Mat();
        Cv2.InRange(hsv, new Scalar(95, 14, 82), new Scalar(130, 105, 200), corridor);

        room.SetTo(Scalar.Black, exclusion);
        corridor.SetTo(Scalar.Black, exclusion);

        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));

        Cv2.MorphologyEx(room, room, MorphTypes.Open, k5);
        Cv2.MorphologyEx(room, room, MorphTypes.Close, k3);

        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Open, k5);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Close, k3);

        return (room, corridor);
    }

    private static Mat SemanticCandidateEdges(Mat mask)
    {
        var output = Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();
        Cv2.FindContours(
            mask,
            out var contours,
            out var hierarchy,
            RetrievalModes.CComp,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0 || hierarchy.Length == 0)
            return output;

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
            Cv2.DrawContours(
                output,
                [approx],
                contourIdx: -1,
                color: Scalar.White,
                thickness: 2,
                lineType: LineTypes.Link8);
        }

        return output;
    }

    private static Mat StrongSourceEdgeSupport(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var strong = new Mat();
        Cv2.Canny(gray, strong, 80d, 180d, apertureSize: 3, L2gradient: true);
        var support = new Mat();
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(strong, support, k5);
        return support;
    }

    private static Point[] SampleSparseEdgePoints(Mat edges, int maxPts)
    {
        if (maxPts <= 0)
            return [];

        // Fast scan collecting edge pixel coordinates
        var points = new List<Point>(Math.Min(maxPts * 4, 1024));
        var width = edges.Width;
        var height = edges.Height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (edges.At<byte>(y, x) > 128)
                {
                    points.Add(new Point(x, y));
                }
            }
        }

        if (points.Count <= maxPts)
            return points.ToArray();

        // Uniform subsampling without LINQ allocations
        var result = new Point[maxPts];
        var step = (double)points.Count / maxPts;
        for (var i = 0; i < maxPts; i++)
        {
            var idx = (int)(i * step);
            result[i] = points[idx];
        }

        return result;
    }
}
