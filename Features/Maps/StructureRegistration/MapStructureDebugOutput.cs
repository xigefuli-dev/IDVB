using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureDebugOutput
{
    internal static string ResolveDebugDirectory(string? requested)
    {
        var directory = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(
                global::IDVBuff.AppDataPaths.RootDirectory,
                "MapAlignmentDebug",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))
            : Path.GetFullPath(requested);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void WritePreprocessDebug(
        string? directory,
        Mat liveRoi,
        MapStructureFeatures live,
        MapStructureFeatures reference)
    {
        if (directory is null)
            return;
        TryWrite(Path.Combine(directory, "01-roi.png"), liveRoi);
        TryWrite(Path.Combine(directory, "02-dynamic-mask.png"), live.NuisanceMask);
        TryWrite(Path.Combine(directory, "03-structure-mask.png"), live.StructureMask);
        TryWrite(Path.Combine(directory, "04-edges.png"), live.Edges);
        TryWrite(
            Path.Combine(directory, "05-reference-structure.png"),
            reference.StructureMask);
    }

    internal static void WriteSearchDebug(
        string? directory,
        MapStructureFeatures reference,
        Mat? heatmap,
        QueryGeometry? query,
        IReadOnlyList<MapStructureCandidate> candidates)
    {
        if (directory is null)
            return;
        if (heatmap is not null && !heatmap.Empty())
        {
            using var normalizedFloat = new Mat();
            using var normalized = new Mat();
            Cv2.Normalize(
                heatmap,
                normalizedFloat,
                255d,
                0d,
                NormTypes.MinMax);
            normalizedFloat.ConvertTo(normalized, MatType.CV_8UC1);
            TryWrite(Path.Combine(directory, "06-search-heatmap.png"), normalized);
        }
        using var visual = new Mat();
        Cv2.CvtColor(reference.StructureMask, visual, ColorConversionCodes.GRAY2BGR);
        if (query is not null)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                Cv2.Rectangle(
                    visual,
                    new Rect(
                        candidate.ReferenceX,
                        candidate.ReferenceY,
                        Math.Min(query.Bounds.Width, visual.Width - candidate.ReferenceX),
                        Math.Min(query.Bounds.Height, visual.Height - candidate.ReferenceY)),
                    index == 0 ? Scalar.LimeGreen : Scalar.OrangeRed,
                    index == 0 ? 3 : 1);
            }
        }
        TryWrite(Path.Combine(directory, "07-top-candidates.png"), visual);
    }

    internal static void WriteFinalDebug(
        string? directory,
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        MapOverlayTransform transform)
    {
        if (directory is null)
            return;
        using var projected = new Mat();
        using var matrix = Mat.Zeros(2, 3, MatType.CV_64FC1).ToMat();
        matrix.Set(0, 0, transform.ScaleX);
        matrix.Set(0, 2, transform.OffsetX - request.ViewportBounds.X);
        matrix.Set(1, 1, transform.ScaleY);
        matrix.Set(1, 2, transform.OffsetY - request.ViewportBounds.Y);
        Cv2.WarpAffine(
            reference.Edges,
            projected,
            matrix,
            request.LiveRoi.Size(),
            InterpolationFlags.Nearest,
            BorderTypes.Constant,
            Scalar.Black);
        using var visual = new Mat(
            request.LiveRoi.Size(),
            MatType.CV_8UC3,
            Scalar.Black);
        visual.SetTo(new Scalar(0, 170, 0), live.Edges);
        visual.SetTo(new Scalar(0, 0, 220), projected);
        using var overlap = new Mat();
        Cv2.BitwiseAnd(live.Edges, projected, overlap);
        visual.SetTo(new Scalar(0, 255, 255), overlap);
        TryWrite(Path.Combine(directory, "08-final-overlay.png"), visual);
    }

    internal static void TryWrite(string path, Mat image)
    {
        try
        {
            Cv2.ImWrite(path, image);
        }
        catch
        {
            // Debug output must never decide whether a transform is accepted.
        }
    }
}
