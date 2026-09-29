using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record MapEntryAuthorEvidence(bool Verified, string Reason, int FloorPixels,
    int EdgePixels, double Median, double P90, double WithinThree, double WithinSix,
    double FloorExplained, bool OpposingWallConflict);

internal static partial class MapEntryIdentityValidation
{
    public static MapEntryAuthorEvidence VerifyAuthor(MapEntryIdentityFrame frame,
        Mat reference, MapEntryIdentityPose pose, Func<bool> canCompute)
    {
        var floorPixels = Cv2.CountNonZero(frame.Floor);
        MapEntryAuthorEvidence Fail(string reason) => new(false, reason, floorPixels, 0,
            double.PositiveInfinity, double.PositiveInfinity, 0, 0, 0, false);
        if (!canCompute()) return Fail("author-budget-incomplete");
        if (floorPixels < 1500) return Fail("insufficient-current-map-pixels");
        using var projected = Project(reference, pose, frame.Floor.Size());
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var projectedEdges = new Mat();
        Cv2.MorphologyEx(projected, projectedEdges, MorphTypes.Gradient, k3);
        var count = Cv2.CountNonZero(frame.AuthorObserved);
        if (count < 200 || Cv2.CountNonZero(projectedEdges) == 0)
            return Fail("insufficient-wall-edges");
        using var distance = Distances(projectedEdges);
        var values = AtMask(distance, frame.AuthorObserved);
        var summary = Residual(values);
        var queryNormals = NormalSupports(frame.Floor);
        var sourceNormals = NormalSupports(projected);
        var normals = new (double Median, double Six)?[4];
        try
        {
            for (var i = 0; i < 4; i++)
            {
                if (!canCompute()) return Fail("author-budget-incomplete");
                Cv2.BitwiseAnd(queryNormals[i], frame.AuthorObserved, queryNormals[i]);
                if (Cv2.CountNonZero(queryNormals[i]) < 200) continue;
                Cv2.BitwiseAnd(sourceNormals[i], projectedEdges, sourceNormals[i]);
                if (Cv2.CountNonZero(sourceNormals[i]) == 0)
                    normals[i] = (double.PositiveInfinity, 0);
                else
                {
                    using var field = Distances(sourceNormals[i]);
                    var metric = Residual(AtMask(field, queryNormals[i]));
                    normals[i] = (metric.Median, metric.Six);
                }
            }
        }
        finally
        {
            foreach (var mat in queryNormals.Concat(sourceNormals)) mat.Dispose();
        }
        var conflict = Enumerable.Range(0, 2).Any(axis =>
            normals[axis * 2] is { } first && normals[axis * 2 + 1] is { } second
            && first.Six < .62 && second.Six < .62 && (first.Median > 6 || second.Median > 6));
        using var k7 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        using var explained = new Mat();
        Cv2.Dilate(projected, explained, k7);
        Cv2.BitwiseAnd(explained, frame.Floor, explained);
        var fraction = Cv2.CountNonZero(explained) / (double)floorPixels;
        var verified = canCompute() && summary.Median <= 4.5 && summary.Three >= .45
            && summary.Six >= .62 && fraction >= .68 && !conflict;
        return new(verified, verified ? "selected-author-current-frame-verified" : "selected-author-current-frame-failed",
            floorPixels, count, summary.Median, summary.P90, summary.Three, summary.Six, fraction, conflict);
    }

    public static bool VerifyWallRaster(MapEntryIdentityFrame frame, Mat clean, Mat walls,
        Mat unknown, MapEntryIdentityPose pose, Func<bool> canCompute)
    {
        if (frame.Walls.Count == 0 || !canCompute()) return false;
        using var projected = Project(clean, pose, frame.Query.Size());
        using var divided = clean.Clone();
        divided.SetTo(Scalar.Black, walls);
        using var interior = Project(divided, pose, frame.Query.Size());
        using var projectedUnknown = Project(unknown, pose, frame.Query.Size());
        var outerNormals = NormalSupports(projected);
        var innerNormals = NormalSupports(interior);
        var fields = new Mat[4];
        try
        {
            for (var i = 0; i < 4; i++)
            {
                if (!canCompute()) return false;
                Cv2.BitwiseOr(outerNormals[i], innerNormals[i], outerNormals[i]);
                outerNormals[i].SetTo(Scalar.Black, projectedUnknown);
                fields[i] = Distances(outerNormals[i]);
            }
            var measured = 0;
            foreach (var wall in frame.Walls)
            {
                if (!canCompute()) return false;
                var direction = wall.Axis == 0 ? wall.Sign > 0 ? 0 : 1 : wall.Sign > 0 ? 3 : 2;
                for (var along = (int)Math.Ceiling(wall.Lo); along <= Math.Floor(wall.Hi); along++)
                {
                    var normal = (int)Math.Round(wall.Normal);
                    var x = wall.Axis == 0 ? normal : along;
                    var y = wall.Axis == 0 ? along : normal;
                    if (x < 0 || y < 0 || x >= frame.Query.Width || y >= frame.Query.Height) return false;
                    if (projectedUnknown.At<byte>(y, x) != 0) continue;
                    measured++;
                    if (fields[direction].At<float>(y, x) > 6) return false;
                }
            }
            return measured > 0 && canCompute();
        }
        finally
        {
            foreach (var mat in outerNormals.Concat(innerNormals).Concat(fields)) mat?.Dispose();
        }
    }

    internal static Mat Project(Mat source, MapEntryIdentityPose pose, Size size)
    {
        using var matrix = new Mat(2, 3, MatType.CV_64F, Scalar.All(0));
        matrix.Set(0, 0, pose.Scale); matrix.Set(1, 1, pose.Scale);
        matrix.Set(0, 2, pose.Tx); matrix.Set(1, 2, pose.Ty);
        var result = new Mat();
        Cv2.WarpAffine(source, result, matrix, size, InterpolationFlags.Nearest,
            BorderTypes.Constant, Scalar.Black);
        Cv2.Threshold(result, result, 0, 255, ThresholdTypes.Binary);
        return result;
    }

    internal static Mat[] NormalSupports(Mat mask)
    {
        using var x = new Mat(); using var y = new Mat();
        using var ax = new Mat(); using var ay = new Mat();
        using var horizontal = new Mat(); using var vertical = new Mat();
        Cv2.Sobel(mask, x, MatType.CV_32F, 1, 0);
        Cv2.Sobel(mask, y, MatType.CV_32F, 0, 1);
        Cv2.Absdiff(x, Scalar.All(0), ax); Cv2.Absdiff(y, Scalar.All(0), ay);
        Cv2.Compare(ax, ay, horizontal, CmpTypes.GE);
        Cv2.Compare(ay, ax, vertical, CmpTypes.GE);
        var fields = Enumerable.Range(0, 4).Select(_ => new Mat()).ToArray();
        Cv2.Compare(x, 0, fields[0], CmpTypes.GT);
        Cv2.Compare(x, 0, fields[1], CmpTypes.LT);
        Cv2.Compare(y, 0, fields[2], CmpTypes.GT);
        Cv2.Compare(y, 0, fields[3], CmpTypes.LT);
        for (var i = 0; i < 4; i++) Cv2.BitwiseAnd(fields[i], i < 2 ? horizontal : vertical, fields[i]);
        return fields;
    }

    private static Mat Distances(Mat support)
    {
        using var inverse = new Mat();
        Cv2.Threshold(support, inverse, 0, 255, ThresholdTypes.BinaryInv);
        var distance = new Mat();
        Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        return distance;
    }

    private static double[] AtMask(Mat values, Mat mask)
    {
        using var points = mask.FindNonZero();
        var result = new double[(int)points.Total()];
        for (var i = 0; i < result.Length; i++)
        {
            var point = points.At<Point>(i);
            result[i] = values.At<float>(point.Y, point.X);
        }
        return result;
    }

    private static (double Median, double P90, double Three, double Six) Residual(double[] values)
    {
        if (values.Length == 0) return (double.PositiveInfinity, double.PositiveInfinity, 0, 0);
        Array.Sort(values);
        var at = (values.Length - 1) * .9;
        return ((values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2,
            values[(int)at] + (at - (int)at) * (values[(int)Math.Ceiling(at)] - values[(int)at]),
            values.Count(v => v <= 3) / (double)values.Length, values.Count(v => v <= 6) / (double)values.Length);
    }
}
