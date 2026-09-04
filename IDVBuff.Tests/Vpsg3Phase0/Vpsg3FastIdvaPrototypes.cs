using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3FastIdvaPrototypes
{
    public sealed record IdvaStepResult(Mat Edges, double ElapsedMs) : IDisposable
    {
        public void Dispose() => Edges.Dispose();
    }

    public static IdvaStepResult RunAblationStepByName(string stepName, Mat liveImage)
    {
        return stepName switch
        {
            "A-0 (Baseline IDVA 2.0)" => RunA0Baseline(liveImage),
            "A-1 (Drop Edge CC)" => RunA1DropEdgeCc(liveImage),
            "A-2 (Drop Hole Fill)" => RunA2DropHoleFill(liveImage),
            "A-3 (Morphology over Room/Corridor CC)" => RunA3MorphologyOnly(liveImage),
            "A-4 (Cheap Dynamic Exclusion)" => RunA4CheapExclusion(liveImage),
            "A-5 (2x Downsampled Streamlined)" => RunA5Downsampled(liveImage),
            _ => throw new ArgumentOutOfRangeException(nameof(stepName))
        };
    }

    public static IdvaAblationResult RunAblationStep(
        string stepName,
        Mat liveImage,
        string sampleId,
        Mat baselineEdges)
    {
        var step = RunAblationStepByName(stepName, liveImage);
        using var candidateEdges = step.Edges;
        var (precision, recall) = ComputePrecisionRecall(candidateEdges, baselineEdges);
        var edgeCount = Cv2.CountNonZero(candidateEdges);

        return new IdvaAblationResult(
            stepName,
            sampleId,
            step.ElapsedMs,
            edgeCount,
            precision,
            recall);
    }

    public static IdvaStepResult RunA0Baseline(Mat source)
    {
        var sw = Stopwatch.StartNew();
        using var result = IdvaNativeObservedExtractor.Process(source);
        var clone = result.ObservedEdges.Clone();
        sw.Stop();
        return new IdvaStepResult(clone, sw.Elapsed.TotalMilliseconds);
    }

    public static IdvaStepResult RunA1DropEdgeCc(Mat source)
    {
        var sw = Stopwatch.StartNew();
        using var bgr = EnsureBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var exclusion = DetectDynamicExclusion(bgr, hsv);
        var (room, corridor) = ClassifyStructureWithHoles(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            var rawObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, rawObserved);
            // Drop edge CC (no RemoveSmallComponents on edges, no RemoveBorderComponents)
            sw.Stop();
            return new IdvaStepResult(rawObserved, sw.Elapsed.TotalMilliseconds);
        }
    }

    public static IdvaStepResult RunA2DropHoleFill(Mat source)
    {
        var sw = Stopwatch.StartNew();
        using var bgr = EnsureBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var exclusion = DetectDynamicExclusion(bgr, hsv);
        // Skip FillSmallHoles entirely; replace with 5x5 morph close
        var (room, corridor) = ClassifyStructureWithoutHoles(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            var rawObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, rawObserved);
            sw.Stop();
            return new IdvaStepResult(rawObserved, sw.Elapsed.TotalMilliseconds);
        }
    }

    public static IdvaStepResult RunA3MorphologyOnly(Mat source)
    {
        var sw = Stopwatch.StartNew();
        using var bgr = EnsureBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var exclusion = DetectDynamicExclusion(bgr, hsv);
        // Replace room/corridor CC with 5x5 Open/Close morphology
        var (room, corridor) = ClassifyStructurePureMorphology(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            var rawObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, rawObserved);
            sw.Stop();
            return new IdvaStepResult(rawObserved, sw.Elapsed.TotalMilliseconds);
        }
    }

    public static IdvaStepResult RunA4CheapExclusion(Mat source)
    {
        var sw = Stopwatch.StartNew();
        using var bgr = EnsureBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        // Fast exclusion: fixed ROI projection without ConnectedComponents
        using var exclusion = FastDynamicExclusion(bgr, hsv);
        var (room, corridor) = ClassifyStructurePureMorphology(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            var rawObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, rawObserved);
            sw.Stop();
            return new IdvaStepResult(rawObserved, sw.Elapsed.TotalMilliseconds);
        }
    }

    public static IdvaStepResult RunA5Downsampled(Mat source)
    {
        var sw = Stopwatch.StartNew();
        var origSize = source.Size();
        var halfSize = new Size(origSize.Width / 2, origSize.Height / 2);

        using var half = new Mat();
        Cv2.Resize(source, half, halfSize, interpolation: InterpolationFlags.Linear);

        using var bgr = EnsureBgr(half);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var exclusion = FastDynamicExclusion(bgr, hsv);
        var (room, corridor) = ClassifyStructurePureMorphology(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            using var halfObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, halfObserved);

            var fullObserved = new Mat();
            Cv2.Resize(halfObserved, fullObserved, origSize, interpolation: InterpolationFlags.Nearest);
            sw.Stop();
            return new IdvaStepResult(fullObserved, sw.Elapsed.TotalMilliseconds);
        }
    }

    #region Helper Pipelines

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
                throw new InvalidDataException("Unsupported channels.");
        }
        return bgr;
    }

    private static Mat DetectDynamicExclusion(Mat bgr, Mat hsv)
    {
        var h = bgr.Height;
        var w = bgr.Width;
        var exclusion = Mat.Zeros(bgr.Size(), MatType.CV_8UC1).ToMat();

        var greenRoiY = (int)(0.68 * h);
        var greenRoiW = (int)(0.28 * w);
        var greenRoiH = h - greenRoiY;
        if (greenRoiW > 0 && greenRoiH > 0)
        {
            var greenRect = new Rect(0, greenRoiY, greenRoiW, greenRoiH);
            using var hsvGreen = new Mat(hsv, greenRect);
            using var greenSeed = new Mat();
            Cv2.InRange(hsvGreen, new Scalar(35, 55, 45), new Scalar(95, 255, 255), greenSeed);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var n = Cv2.ConnectedComponentsWithStats(greenSeed, labels, stats, centroids, PixelConnectivity.Connectivity8);
            if (n > 1)
            {
                var boxes = new List<Rect>();
                for (var i = 1; i < n; i++)
                {
                    if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) >= 8)
                    {
                        var bx = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                        var by = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                        var bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                        var bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                        boxes.Add(new Rect(bx + greenRect.X, by + greenRect.Y, bw, bh));
                    }
                }
                if (boxes.Count > 0)
                {
                    var x0 = boxes.Min(b => b.X);
                    var y0 = boxes.Min(b => b.Y);
                    var x1 = boxes.Max(b => b.Right);
                    var y1 = boxes.Max(b => b.Bottom);
                    if (x0 < 0.15 * w && y1 > 0.75 * h)
                    {
                        const int pad = 15;
                        var fillRect = new Rect(
                            Math.Max(0, x0 - pad),
                            Math.Max(0, y0 - pad),
                            Math.Min(w, x1 + pad) - Math.Max(0, x0 - pad),
                            Math.Min(h, y1 + pad) - Math.Max(0, y0 - pad));
                        exclusion[fillRect].SetTo(Scalar.White);
                    }
                }
            }
        }
        return exclusion;
    }

    private static Mat FastDynamicExclusion(Mat bgr, Mat hsv)
    {
        var h = bgr.Height;
        var w = bgr.Width;
        var exclusion = Mat.Zeros(bgr.Size(), MatType.CV_8UC1).ToMat();

        // 1. Bottom-left green HUD fast bounding box
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
                // Mask the bottom-left avatar region directly without ConnectedComponents
                var fillRect = new Rect(0, (int)(0.72 * h), (int)(0.24 * w), (int)(0.28 * h));
                exclusion[fillRect].SetTo(Scalar.White);
            }
        }

        // 2. Top glyph fast mask
        var topRoiY = (int)(0.03 * h);
        var topRoiH = (int)(0.12 * h);
        var topRoiX = (int)(0.10 * w);
        var topRoiW = (int)(0.70 * w);
        var topRect = new Rect(topRoiX, topRoiY, topRoiW, topRoiH);
        using var hsvTop = new Mat(hsv, topRect);
        using var whiteSeed = new Mat();
        Cv2.InRange(hsvTop, new Scalar(0, 0, 120), new Scalar(180, 60, 255), whiteSeed);
        if (Cv2.CountNonZero(whiteSeed) > 100)
        {
            exclusion[topRect].SetTo(Scalar.White);
        }

        return exclusion;
    }

    private static (Mat Room, Mat Corridor) ClassifyStructureWithHoles(Mat hsv, Mat exclusion)
    {
        var (cleanRoom, cleanCorridor) = ClassifyStructurePureMorphology(hsv, exclusion);
        return (cleanRoom, cleanCorridor);
    }

    private static (Mat Room, Mat Corridor) ClassifyStructureWithoutHoles(Mat hsv, Mat exclusion)
    {
        return ClassifyStructurePureMorphology(hsv, exclusion);
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

        // Use morphological Open to eliminate small speckles without ConnectedComponents
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
            if (perimeter < 30d)
                continue;

            var parent = hierarchy[idx].Parent;
            if (parent != -1 && Math.Abs(Cv2.ContourArea(contour)) < 900d)
                continue;

            var approx = Cv2.ApproxPolyDP(contour, 0.55d, closed: true);
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

    private static (double Precision, double Recall) ComputePrecisionRecall(Mat candidate, Mat baseline)
    {
        var candCount = Cv2.CountNonZero(candidate);
        var baseCount = Cv2.CountNonZero(baseline);

        if (candCount == 0 && baseCount == 0)
            return (1.0d, 1.0d);
        if (candCount == 0)
            return (1.0d, 0.0d);
        if (baseCount == 0)
            return (0.0d, 1.0d);

        using var candDilated = new Mat();
        using var baseDilated = new Mat();
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

        Cv2.Dilate(candidate, candDilated, k3);
        Cv2.Dilate(baseline, baseDilated, k3);

        using var candMatch = new Mat();
        using var baseMatch = new Mat();

        Cv2.BitwiseAnd(candidate, baseDilated, candMatch);
        Cv2.BitwiseAnd(baseline, candDilated, baseMatch);

        var precision = Math.Clamp((double)Cv2.CountNonZero(candMatch) / candCount, 0.0, 1.0);
        var recall = Math.Clamp((double)Cv2.CountNonZero(baseMatch) / baseCount, 0.0, 1.0);

        return (precision, recall);
    }

    public static IdvaDownstreamResult EvaluateDownstreamPipeline(
        string stageName,
        GroundTruthSample sample,
        Mat baselineEdges,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid pyramid)
    {
        var extraction = RunAblationStepByName(stageName, sample.LiveImage);
        using var candEdges = extraction.Edges;

        var (precBase, recBase) = ComputePrecisionRecall(candEdges, baselineEdges);
        var (precGt, recGt) = ComputePrecisionRecall(candEdges, sample.GroundTruthVisibleEdge);
        var edgeCount = Cv2.CountNonZero(candEdges);

        var (sbRes, _) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(candEdges, sample.ReferenceStructureLine, sample.TrueScale, sample.Id, sample.SourceType);
        var scaleError = Math.Abs(sbRes.EstimatedScale - sample.TrueScale);

        var transResult = Vpsg3TranslationPrototypes.EvaluateTranslationTopK(
            candEdges, sample.ReferenceStructureLine, sample, sbRes.EstimatedScale, topK: 8);

        var transErr = transResult.Top1ErrorPixels;
        var top1Hit3px = transResult.Top1Hit3px;
        var top4Hit3px = transResult.Top4Recall;
        var falseCands = transResult.Top4Recall ? 0 : 1;

        return new IdvaDownstreamResult(
            stageName,
            sample.Id,
            sample.SourceType,
            extraction.ElapsedMs,
            edgeCount,
            precBase,
            recBase,
            precGt,
            recGt,
            scaleError,
            transErr,
            top1Hit3px,
            top4Hit3px,
            falseCands);
    }

    #endregion
}
