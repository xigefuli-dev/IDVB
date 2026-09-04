using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Native C# implementation of the dedicated IDVA algorithm:
/// "structure.native-observed.v2" (第五人格原生地图可观测结构线图).
/// Extracts ObservedEdges and ValidMask from a captured live game viewport image.
/// </summary>
public sealed class IdvaNativeObservedExtractor
{
    private const string AlgorithmId = "structure.native-observed.v2";
    private const string PackageSha256 =
        "BA418F4571B287CE21C36F5793DFFD5EAFBCDB23B73EF65087440B3ABF530246";
    private static readonly Lazy<bool> Definition = new(ValidateDefinition);

    public sealed record Result(Mat ObservedEdges, Mat ValidMask) : IDisposable
    {
        public void Dispose()
        {
            ObservedEdges.Dispose();
            ValidMask.Dispose();
        }
    }

    public static Result Process(Mat source)
    {
        _ = Definition.Value;
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new InvalidDataException("IDVA 实时输入图像不能为空。");

        using var bgr = new Mat();
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
                throw new InvalidDataException("IDVA 实时输入只支持灰度、BGR 或 BGRA 图像。");
        }

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var exclusion = DetectDynamicExclusion(bgr, hsv);
        var (room, corridor) = ClassifyStructure(hsv, exclusion);
        using (room)
        using (corridor)
        {
            using var roomEdges = SemanticCandidateEdges(room);
            using var corridorEdges = SemanticCandidateEdges(corridor);
            using var candidateEdges = new Mat();
            Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);

            using var support = StrongSourceEdgeSupport(bgr);

            using var rawObserved = new Mat();
            Cv2.BitwiseAnd(candidateEdges, support, rawObserved);
            using var retainedEdges = RemoveSmallComponents(rawObserved, 6);
            var observedEdges = RemoveBorderComponents(retainedEdges);

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

            return new Result(observedEdges, validMask);
        }
    }

    private static bool ValidateDefinition()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Algorithms",
            "structure-native-observed-v2.idva");
        if (!File.Exists(path))
            throw new InvalidDataException($"缺少实时结构算法包：{path}");

        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(
            Convert.ToHexString(SHA256.HashData(bytes)),
            PackageSha256,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("实时结构算法包校验失败，拒绝使用不匹配的 IDVA。");
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "IDVA"
            || root.GetProperty("schema_version").GetString() != "1.1"
            || root.GetProperty("algorithm_id").GetString() != AlgorithmId
            || !root.GetProperty("geometry_policy")
                .GetProperty("preserve_input_size").GetBoolean()
            || !root.GetProperty("outputs").TryGetProperty("ObservedEdges", out _)
            || !root.GetProperty("outputs").TryGetProperty("ValidMask", out _))
        {
            throw new InvalidDataException("实时结构 IDVA 的身份、几何或输出契约无效。");
        }

        return true;
    }

    private static Mat DetectDynamicExclusion(Mat bgr, Mat hsv)
    {
        var h = bgr.Height;
        var w = bgr.Width;
        var exclusion = Mat.Zeros(bgr.Size(), MatType.CV_8UC1).ToMat();

        // 1. Bottom-left green HUD
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
                    var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area >= 8)
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

        // 2. Top status glyph group
        var topRoiY = (int)(0.03 * h);
        var topRoiH = (int)(0.20 * h);
        var topRoiX = (int)(0.08 * w);
        var topRoiW = (int)(0.72 * w);
        if (topRoiW > 0 && topRoiH > 0)
        {
            var topRect = new Rect(topRoiX, topRoiY, topRoiW, topRoiH);
            using var hsvTop = new Mat(hsv, topRect);
            using var whiteSeed = new Mat();
            Cv2.InRange(hsvTop, new Scalar(0, 0, 115), new Scalar(180, 70, 255), whiteSeed);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var n = Cv2.ConnectedComponentsWithStats(whiteSeed, labels, stats, centroids, PixelConnectivity.Connectivity8);
            if (n > 1)
            {
                var glyphs = new List<Rect>();
                for (var i = 1; i < n; i++)
                {
                    var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    var bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                    var bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                    if (bw is >= 14 and <= 48 && bh is >= 14 and <= 48 && area is >= 120 and <= 900)
                    {
                        var bx = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                        var by = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                        glyphs.Add(new Rect(bx + topRect.X, by + topRect.Y, bw, bh));
                    }
                }

                List<Rect> bestGroup = [];
                foreach (var g in glyphs)
                {
                    var group = glyphs.Where(q => Math.Abs(q.Y - g.Y) <= 5 && Math.Abs(q.Height - g.Height) <= 8).ToList();
                    if (group.Count > bestGroup.Count)
                        bestGroup = group;
                }

                if (bestGroup.Count >= 3)
                {
                    var x0 = bestGroup.Min(b => b.X);
                    var y0 = bestGroup.Min(b => b.Y);
                    var x1 = bestGroup.Max(b => b.Right);
                    var y1 = bestGroup.Max(b => b.Bottom);

                    if (x1 - x0 >= 80)
                    {
                        var fillRect = new Rect(
                            Math.Max(0, x0 - 70),
                            Math.Max(0, y0 - 40),
                            Math.Min(w, x1 + 70) - Math.Max(0, x0 - 70),
                            Math.Min(h, y1 + 40) - Math.Max(0, y0 - 40));
                        exclusion[fillRect].SetTo(Scalar.White);
                    }
                }
            }
        }

        return exclusion;
    }

    private static (Mat Room, Mat Corridor) ClassifyStructure(Mat hsv, Mat exclusion)
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
        Cv2.MorphologyEx(room, room, MorphTypes.Open, k3);
        Cv2.MorphologyEx(room, room, MorphTypes.Close, k3);

        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Open, k3);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Close, k3);

        var cleanRoom = RemoveSmallComponents(room, 80);
        room.Dispose();
        var cleanCorridor = RemoveSmallComponents(corridor, 80);
        corridor.Dispose();

        var filledRoom = FillSmallHoles(cleanRoom, 450);
        cleanRoom.Dispose();
        var filledCorridor = FillSmallHoles(cleanCorridor, 450);
        cleanCorridor.Dispose();

        return (filledRoom, filledCorridor);
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

    private static Mat RemoveSmallComponents(Mat mask, int minArea)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        if (n <= 1)
            return Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();

        var keep = new bool[n];
        for (var i = 1; i < n; i++)
            keep[i] = stats.At<int>(i, (int)ConnectedComponentsTypes.Area) >= minArea;

        var total = mask.Width * mask.Height;
        var labelArray = new int[total];
        Marshal.Copy(labels.Data, labelArray, 0, total);

        var output = Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();
        var outputArray = new byte[total];
        for (var i = 0; i < total; i++)
        {
            var label = labelArray[i];
            if (label > 0 && keep[label])
                outputArray[i] = 255;
        }

        Marshal.Copy(outputArray, 0, output.Data, total);
        return output;
    }

    private static Mat RemoveBorderComponents(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var border = Math.Max(
            1,
            (int)Math.Round(Math.Min(mask.Width, mask.Height) * 0.02d));
        var keep = new bool[count];
        for (var index = 1; index < count; index++)
        {
            var left = stats.At<int>(index, (int)ConnectedComponentsTypes.Left);
            var top = stats.At<int>(index, (int)ConnectedComponentsTypes.Top);
            var width = stats.At<int>(index, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(index, (int)ConnectedComponentsTypes.Height);
            keep[index] = left >= border
                && top >= border
                && left + width <= mask.Width - border
                && top + height <= mask.Height - border;
        }

        var total = mask.Width * mask.Height;
        var labelArray = new int[total];
        Marshal.Copy(labels.Data, labelArray, 0, total);
        var outputArray = new byte[total];
        for (var index = 0; index < total; index++)
        {
            var label = labelArray[index];
            if (label > 0 && keep[label])
                outputArray[index] = 255;
        }

        var output = Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();
        Marshal.Copy(outputArray, 0, output.Data, total);
        return output;
    }

    private static Mat FillSmallHoles(Mat mask, int maxArea)
    {
        using var inv = new Mat();
        Cv2.BitwiseNot(mask, inv);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(inv, labels, stats, centroids, PixelConnectivity.Connectivity8);
        if (n <= 1)
            return mask.Clone();

        var h = mask.Height;
        var w = mask.Width;
        var total = w * h;
        var labelArray = new int[total];
        Marshal.Copy(labels.Data, labelArray, 0, total);

        var borderLabels = new HashSet<int>();
        for (var x = 0; x < w; x++)
        {
            borderLabels.Add(labelArray[x]);
            borderLabels.Add(labelArray[(h - 1) * w + x]);
        }
        for (var y = 0; y < h; y++)
        {
            borderLabels.Add(labelArray[y * w]);
            borderLabels.Add(labelArray[y * w + (w - 1)]);
        }

        var fill = new bool[n];
        for (var i = 1; i < n; i++)
        {
            if (!borderLabels.Contains(i) && stats.At<int>(i, (int)ConnectedComponentsTypes.Area) <= maxArea)
                fill[i] = true;
        }

        var output = mask.Clone();
        var outputArray = new byte[total];
        Marshal.Copy(output.Data, outputArray, 0, total);
        for (var i = 0; i < total; i++)
        {
            var label = labelArray[i];
            if (label > 0 && fill[label])
                outputArray[i] = 255;
        }

        Marshal.Copy(outputArray, 0, output.Data, total);
        return output;
    }
}
