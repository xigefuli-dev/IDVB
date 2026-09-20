using System.Diagnostics;
using System.Numerics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static partial class Vpsg3TranslationPrototypes
{
    #region Helpers
    private static List<Point> ExtractHarrisCorners(Mat edgeImage, int maxCorners)
    {
        using var corners = new Mat();
        Cv2.CornerHarris(edgeImage, corners, blockSize: 3, ksize: 3, k: 0.04);
        Cv2.Normalize(corners, corners, 0, 255, NormTypes.MinMax);

        var list = new List<(Point Pt, float Val)>();
        var w = corners.Width;
        var h = corners.Height;
        for (var y = 3; y < h - 3; y += 2)
        {
            for (var x = 3; x < w - 3; x += 2)
            {
                var val = corners.At<float>(y, x);
                if (val > 80.0f)
                    list.Add((new Point(x, y), val));
            }
        }

        return list.OrderByDescending(p => p.Val).Take(maxCorners).Select(p => p.Pt).ToList();
    }

    private static int ExtractCornerToken(Mat image, Point pt)
    {
        // Sample 8-neighborhood at radius 4
        var token = 0;
        var w = image.Width;
        var h = image.Height;
        var angles = new[] { 0, 45, 90, 135, 180, 225, 270, 315 };
        for (var i = 0; i < 8; i++)
        {
            var rad = angles[i] * Math.PI / 180.0;
            var nx = pt.X + (int)Math.Round(4.0 * Math.Cos(rad));
            var ny = pt.Y + (int)Math.Round(4.0 * Math.Sin(rad));
            if (nx >= 0 && nx < w && ny >= 0 && ny < h)
            {
                if (image.At<byte>(ny, nx) > 128)
                    token |= (1 << i);
            }
        }
        return token;
    }

    private sealed record Junction(Point Location, string Type, int BranchCount);

    private static List<Junction> ExtractJunctions(Mat edgeImage)
    {
        var junctions = new List<Junction>();
        var corners = ExtractHarrisCorners(edgeImage, 80);

        foreach (var c in corners)
        {
            var token = ExtractCornerToken(edgeImage, c);
            var branchCount = BitOperations.PopCount((uint)token);
            var type = branchCount switch
            {
                2 => "L-Corner",
                3 => "T-Junction",
                >= 4 => "X-Crossing",
                _ => "End-Point"
            };

            if (branchCount >= 2)
            {
                junctions.Add(new Junction(c, type, branchCount));
            }
        }

        return junctions;
    }

    private static List<Point> SamplePoints(Mat edges, int maxPts)
    {
        var pts = new List<Point>();
        var w = edges.Width;
        var h = edges.Height;

        for (var y = 2; y < h - 2; y += 3)
        {
            for (var x = 2; x < w - 2; x += 3)
            {
                if (edges.At<byte>(y, x) > 128)
                    pts.Add(new Point(x, y));
            }
        }

        if (pts.Count <= maxPts)
            return pts;

        var stepSize = (double)pts.Count / maxPts;
        var result = new List<Point>(maxPts);
        for (var i = 0; i < maxPts; i++)
            result.Add(pts[(int)(i * stepSize)]);
        return result;
    }

    #endregion
}


