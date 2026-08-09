// IDVB Remaster — GateTemplateDetector 静态预处理与资源方法

using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class GateTemplateDetector
{
    public static Mat CreateEdges(Mat source)
    {
        using var gray = new Mat();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else if (source.Channels() == 4)
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0d);

        var edges = new Mat();
        Cv2.Canny(
            blurred,
            edges,
            GateTemplateRules.CannyLowThreshold,
            GateTemplateRules.CannyHighThreshold);

        // 形态学清理（连接边缘，消除孤立噪点）
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, closeKernel);

        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Open, openKernel);

        // 小组件过滤：去除玩家图标、宝箱、UI组件等小元素（最小12像素）
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            edges,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8);

        if (count > 1)
        {
            var minArea = 12;
            using var kept = Mat.Zeros(edges.Size(), MatType.CV_8UC1).ToMat();
            for (var label = 1; label < count; label++)
            {
                var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
                if (area >= minArea)
                {
                    using var component = new Mat();
                    Cv2.Compare(labels, label, component, CmpTypes.EQ);
                    Cv2.BitwiseOr(kept, component, kept);
                }
            }
            kept.CopyTo(edges);
        }

        return edges;
    }

    public static Mat CreateMatchImage(Mat source)
    {
        var gray = new Mat();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else if (source.Channels() == 4)
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    public static string ResolveGateAssetPath()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        if (File.Exists(deployed))
            return deployed;
        var workspace = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Gate.png"));
        if (File.Exists(workspace))
            return workspace;
        var current = Path.Combine(Environment.CurrentDirectory, "Assets", "Gate.png");
        return File.Exists(current) ? current : deployed;
    }

    // ── Reference gate icon measurement ────────────────────────────────

    /// <summary>
    /// Runs narrow-band multi-scale template matching in the reference image
    /// around <paramref name="anchorCenter"/> to measure the actual pixel size
    /// of the gate icon. Returns the matched template dimensions (in reference
    /// pixels), or null when no confident match is found.
    /// </summary>
    public static (double Width, double Height)? EstimateReferenceGateIconSize(
        Mat referenceImage,
        Point2d anchorCenter)
    {
        if (referenceImage.Empty())
            return null;

        using var matchImage = CreateMatchImage(referenceImage);
        using var gate = Cv2.ImRead(ResolveGateAssetPath(), ImreadModes.Unchanged);
        if (gate.Empty())
            return null;

        using var gateGray = CreateMatchImage(gate);
        var gateW = (double)gateGray.Width;
        var gateH = (double)gateGray.Height;

        // Narrow band of plausible icon scales on the reference image.
        // Reference images are rendered at a consistent resolution where
        // gate icons typically occupy ~2–5% of the image width.
        double[] scales = [0.16, 0.20, 0.24, 0.28, 0.32, 0.36, 0.40, 0.44];

        double bestScore = 0.5; // minimum confidence
        double bestWidth = 0d;
        double bestHeight = 0d;

        foreach (var scale in scales)
        {
            var width = Math.Max(12, (int)Math.Round(gateW * scale));
            var height = Math.Max(12, (int)Math.Round(gateH * scale));
            if (width >= matchImage.Width || height >= matchImage.Height)
                continue;

            var halfW = width / 2;
            var halfH = height / 2;
            var searchRadius = Math.Max(halfW, halfH) + 12; // small local search

            var roiX = Math.Max(0, (int)Math.Round(anchorCenter.X - searchRadius));
            var roiY = Math.Max(0, (int)Math.Round(anchorCenter.Y - searchRadius));
            var roiW = Math.Min(matchImage.Width - roiX, searchRadius * 2);
            var roiH = Math.Min(matchImage.Height - roiY, searchRadius * 2);
            if (roiW < width || roiH < height)
                continue;

            using var scaledGate = new Mat();
            Cv2.Resize(gateGray, scaledGate, new Size(width, height),
                0d, 0d,
                scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
            using var roi = new Mat(matchImage, new Rect(roiX, roiY, roiW, roiH));
            using var output = new Mat();
            Cv2.MatchTemplate(roi, scaledGate, output, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(output, out _, out var score, out _, out _);

            if (score > bestScore)
            {
                bestScore = score;
                bestWidth = width;
                bestHeight = height;
            }
        }

        if (bestWidth <= 0d || bestHeight <= 0d)
            return null;

        return (bestWidth, bestHeight);
    }
}
