using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvaStructureLineEngine
{
    private static void Classify(
        PipelineState state,
        JsonElement parameters,
        JsonElement stage,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var mode = RequireNonEmptyString(stage, "mode");
        if (mode == "HSV_RANGE")
        {
            using var hsv = new Mat();
            Cv2.CvtColor(state.Bgr, hsv, ColorConversionCodes.BGR2HSV);
            state.ReplaceRoom(InRange(hsv, ReadTriplet(parameters, "room_hsv_lo"), ReadTriplet(parameters, "room_hsv_hi")));
            state.ReplaceCorridor(InRange(hsv, ReadTriplet(parameters, "corridor_hsv_lo"), ReadTriplet(parameters, "corridor_hsv_hi")));
            return;
        }
        if (mode != "OPENCV_LAB_NEAREST_CENTER")
            throw new InvalidDataException($"不支持的 color_classification 模式：{mode}。");
        RequireString(parameters, "lab_distance_metric", "euclidean_on_opencv_lab_8bit_scale");
        using var lab = new Mat();
        Cv2.CvtColor(state.Bgr, lab, ColorConversionCodes.BGR2Lab);
        var roomCenters = ConvertBgrCentersToLab(ReadCenters(parameters, "room_bgr_centers"));
        var corridorCenters = ConvertBgrCentersToLab(ReadCenters(parameters, "corridor_bgr_centers"));
        var threshold = ReadBoundedDouble(parameters, "lab_distance_threshold", 0.01d, 255d);
        var roomDistance = MinimumLabDistance(lab, roomCenters, 0d, 0.5d, progress, cancellationToken);
        var corridorDistance = MinimumLabDistance(lab, corridorCenters, 0.5d, 1d, progress, cancellationToken);
        var room = new Mat();
        var corridor = new Mat();
        try
        {
            Cv2.Compare(roomDistance, threshold, room, CmpTypes.LT);
            Cv2.Compare(corridorDistance, threshold, corridor, CmpTypes.LT);
            state.ReplaceRoom(room);
            room = null!;
            state.ReplaceCorridor(corridor);
            corridor = null!;
            state.ReplaceDistances(roomDistance, corridorDistance);
            roomDistance = null!;
            corridorDistance = null!;
        }
        finally
        {
            room?.Dispose();
            corridor?.Dispose();
            roomDistance?.Dispose();
            corridorDistance?.Dispose();
        }
    }

    private static Mat MinimumLabDistance(
        Mat lab,
        IReadOnlyList<Vec3b> centers,
        double progressStart,
        double progressEnd,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        Mat? minimum = null;
        try
        {
            for (var index = 0; index < centers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var center = centers[index];
                using var delta = new Mat();
                Cv2.Absdiff(lab, new Scalar(center.Item0, center.Item1, center.Item2), delta);
                var channels = Cv2.Split(delta);
                try
                {
                    using var sum = Mat.Zeros(lab.Size(), MatType.CV_32FC1).ToMat();
                    foreach (var channel in channels)
                    {
                        using var component = new Mat();
                        channel.ConvertTo(component, MatType.CV_32FC1);
                        Cv2.Multiply(component, component, component);
                        Cv2.Add(sum, component, sum);
                    }
                    using var distance = new Mat();
                    Cv2.Sqrt(sum, distance);
                    if (minimum is null)
                        minimum = distance.Clone();
                    else
                        Cv2.Min(minimum, distance, minimum);
                }
                finally
                {
                    foreach (var channel in channels) channel.Dispose();
                }
                progress(progressStart + (progressEnd - progressStart) * (index + 1d) / centers.Count);
            }
            return minimum ?? throw new InvalidDataException("IDVA Lab 颜色中心不能为空。");
        }
        catch
        {
            minimum?.Dispose();
            throw;
        }
    }

    private static void ResolveClassConflict(PipelineState state)
    {
        if (state.RoomDistance is null || state.CorridorDistance is null)
            return;
        using var corridorCloser = new Mat();
        using var roomCloser = new Mat();
        Cv2.Compare(state.CorridorDistance, state.RoomDistance, corridorCloser, CmpTypes.LT);
        Cv2.Compare(state.RoomDistance, state.CorridorDistance, roomCloser, CmpTypes.LT);
        state.Room.SetTo(Scalar.Black, corridorCloser);
        state.Corridor.SetTo(Scalar.Black, roomCloser);
    }

    private static void IgnoreRouteOverlays(PipelineState state, JsonElement parameters)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(state.Bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var routes = Mat.Zeros(hsv.Size(), MatType.CV_8UC1).ToMat();
        var ranges = RequireArray(parameters, "route_hsv_ranges");
        if (ranges.GetArrayLength() is <= 0 or > 8)
            throw new InvalidDataException("IDVA route_hsv_ranges 必须包含 1 到 8 个范围。");
        foreach (var range in ranges.EnumerateArray())
        {
            using var matched = InRange(
                hsv,
                ReadTripletElement(RequireArray(range, "lo"), "route_hsv_ranges.lo"),
                ReadTripletElement(RequireArray(range, "hi"), "route_hsv_ranges.hi"));
            Cv2.BitwiseOr(routes, matched, routes);
        }
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, ReadSize(parameters, "route_mask_dilate_kernel"));
        Cv2.Dilate(routes, routes, kernel);
        using var room = new Mat();
        using var corridor = new Mat();
        var radius = ReadBoundedDouble(parameters, "route_repair_radius_px", 1d, 32d);
        Cv2.Inpaint(state.Room, routes, room, radius, InpaintTypes.Telea);
        Cv2.Inpaint(state.Corridor, routes, corridor, radius, InpaintTypes.Telea);
        ReplaceMasks(state, room.Clone(), corridor.Clone());
    }

    private static Mat InRange(Mat source, int[] lower, int[] upper)
    {
        var result = new Mat();
        Cv2.InRange(source, new Scalar(lower[0], lower[1], lower[2]),
            new Scalar(upper[0], upper[1], upper[2]), result);
        return result;
    }

    private static Vec3b[] ConvertBgrCentersToLab(IReadOnlyList<Vec3b> centers)
    {
        using var bgr = new Mat(centers.Count, 1, MatType.CV_8UC3);
        for (var index = 0; index < centers.Count; index++)
            bgr.Set(index, 0, centers[index]);
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        return Enumerable.Range(0, centers.Count).Select(index => lab.At<Vec3b>(index, 0)).ToArray();
    }

    private static void MorphEach(PipelineState state, MorphTypes type, Size size)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, size);
        Cv2.MorphologyEx(state.Room, state.Room, type, kernel);
        Cv2.MorphologyEx(state.Corridor, state.Corridor, type, kernel);
    }

    private static Mat RemoveSmall(Mat source, int minimumArea)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            source, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var result = Mat.Zeros(source.Size(), MatType.CV_8UC1).ToMat();
        for (var label = 1; label < count; label++)
        {
            if (stats.At<int>(label, (int)ConnectedComponentsTypes.Area) < minimumArea)
                continue;
            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(result, component, result);
        }
        return result;
    }

    private static Mat FillAllHoles(Mat source)
    {
        var result = source.Clone();
        using var inverse = new Mat();
        Cv2.BitwiseNot(source, inverse);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            inverse, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var borderLabels = GetBorderLabels(labels);
        for (var label = 1; label < count; label++)
        {
            if (borderLabels.Contains(label))
                continue;
            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            result.SetTo(Scalar.White, component);
        }
        return result;
    }

    private static Mat FillSmallHoles(Mat source, int maximumArea)
    {
        var result = source.Clone();
        using var inverse = new Mat();
        Cv2.Compare(source, 0, inverse, CmpTypes.EQ);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            inverse, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var borderLabels = GetBorderLabels(labels);
        for (var label = 1; label < count; label++)
        {
            if (!borderLabels.Contains(label)
                && stats.At<int>(label, (int)ConnectedComponentsTypes.Area) <= maximumArea)
            {
                using var component = new Mat();
                Cv2.Compare(labels, label, component, CmpTypes.EQ);
                result.SetTo(Scalar.White, component);
            }
        }
        return result;
    }

    private static HashSet<int> GetBorderLabels(Mat labels)
    {
        var result = new HashSet<int>();
        var rows = labels.Rows;
        var columns = labels.Cols;
        for (var x = 0; x < columns; x++)
        {
            result.Add(labels.At<int>(0, x));
            result.Add(labels.At<int>(rows - 1, x));
        }
        for (var y = 0; y < rows; y++)
        {
            result.Add(labels.At<int>(y, 0));
            result.Add(labels.At<int>(y, columns - 1));
        }
        return result;
    }

    private static Mat DirectionalBridge(Mat source, int horizontalGap, int verticalGap)
    {
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(horizontalGap, 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, verticalGap));
        using var horizontal = new Mat();
        using var vertical = new Mat();
        Cv2.MorphologyEx(source, horizontal, MorphTypes.Close, horizontalKernel);
        Cv2.MorphologyEx(source, vertical, MorphTypes.Close, verticalKernel);
        var result = new Mat();
        Cv2.BitwiseOr(source, horizontal, result);
        Cv2.BitwiseOr(result, vertical, result);
        return result;
    }

    private static Mat DrawContours(Mat mask, RetrievalModes retrieval, int thickness)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, retrieval,
            ContourApproximationModes.ApproxSimple);
        var result = Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();
        if (contours.Length > 0)
            Cv2.DrawContours(result, contours, -1, Scalar.White, thickness, LineTypes.Link8);
        return result;
    }
}
