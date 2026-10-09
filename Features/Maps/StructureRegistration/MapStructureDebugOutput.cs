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
        MapDiagnosticModeCapture.WriteInputs(liveRoi, live.StructureMask);
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
        if (directory is null && MapDiagnosticModeCapture.TryDeferFitness(() =>
            {
                var bounds = query?.Bounds;
                var points = candidates.Select(candidate =>
                    new Point(candidate.ReferenceX, candidate.ReferenceY)).ToArray();
                var structure = reference.StructureMask.Clone();
                return new MapDiagnosticModeCapture.FitnessDrawing(
                    () => RenderSearchVisual(structure, bounds, points), structure);
            }))
            return;
        if (directory is null && !MapDiagnosticModeCapture.IsActive)
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
            MapDiagnosticModeCapture.WriteFitness(normalized);
            if (directory is not null)
                TryWrite(Path.Combine(directory, "06-search-heatmap.png"), normalized);
        }
        using var visual = RenderSearchVisual(reference.StructureMask, query?.Bounds,
            candidates.Select(candidate => new Point(candidate.ReferenceX, candidate.ReferenceY)).ToArray());
        MapDiagnosticModeCapture.WriteFitness(visual);
        if (directory is not null)
            TryWrite(Path.Combine(directory, "07-top-candidates.png"), visual);
    }

    private static Mat RenderSearchVisual(Mat structure, Rect? queryBounds, Point[] candidates)
    {
        var visual = new Mat();
        try
        {
            Cv2.CvtColor(structure, visual, ColorConversionCodes.GRAY2BGR);
            if (queryBounds is { } bounds)
            {
                for (var index = 0; index < candidates.Length; index++)
                {
                    var candidate = candidates[index];
                    Cv2.Rectangle(
                        visual,
                        new Rect(
                            candidate.X,
                            candidate.Y,
                            Math.Min(bounds.Width, visual.Width - candidate.X),
                            Math.Min(bounds.Height, visual.Height - candidate.Y)),
                        index == 0 ? Scalar.LimeGreen : Scalar.OrangeRed,
                        index == 0 ? 3 : 1);
                }
            }
            return visual;
        }
        catch
        {
            visual.Dispose();
            throw;
        }
    }

    internal static void WriteFinalDebug(
        string? directory,
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        MapOverlayTransform transform)
    {
        var size = request.LiveRoi.Size();
        var viewport = request.ViewportBounds;
        var scaleX = transform.ScaleX;
        var scaleY = transform.ScaleY;
        var offsetX = transform.OffsetX - viewport.X;
        var offsetY = transform.OffsetY - viewport.Y;
        if (directory is null && MapDiagnosticModeCapture.TryDeferFitness(() =>
            {
                var edges = reference.Edges.Clone();
                try
                {
                    var liveEdges = live.Edges.Clone();
                    return new MapDiagnosticModeCapture.FitnessDrawing(
                        () => RenderFinalVisual(edges, liveEdges, size,
                            scaleX, scaleY, offsetX, offsetY), edges, liveEdges);
                }
                catch { edges.Dispose(); throw; }
            }))
            return;
        if (directory is null && !MapDiagnosticModeCapture.IsActive)
            return;
        using var visual = RenderFinalVisual(reference.Edges, live.Edges, size,
            scaleX, scaleY, offsetX, offsetY);
        MapDiagnosticModeCapture.WriteFitness(visual);
        if (directory is not null)
            TryWrite(Path.Combine(directory, "08-final-overlay.png"), visual);
    }

    private static Mat RenderFinalVisual(Mat referenceEdges, Mat liveEdges, Size size,
        double scaleX, double scaleY, double offsetX, double offsetY)
    {
        using var projected = new Mat();
        using var matrix = Mat.Zeros(2, 3, MatType.CV_64FC1).ToMat();
        matrix.Set(0, 0, scaleX);
        matrix.Set(0, 2, offsetX);
        matrix.Set(1, 1, scaleY);
        matrix.Set(1, 2, offsetY);
        Cv2.WarpAffine(
            referenceEdges,
            projected,
            matrix,
            size,
            InterpolationFlags.Area,
            BorderTypes.Constant,
            Scalar.Black);
        Cv2.Threshold(projected, projected, 0d, 255d, ThresholdTypes.Binary);
        var visual = new Mat(
            size,
            MatType.CV_8UC3,
            Scalar.Black);
        try
        {
            visual.SetTo(new Scalar(0, 170, 0), liveEdges);
            visual.SetTo(new Scalar(0, 0, 220), projected);
            using var overlap = new Mat();
            Cv2.BitwiseAnd(liveEdges, projected, overlap);
            visual.SetTo(new Scalar(0, 255, 255), overlap);
            return visual;
        }
        catch { visual.Dispose(); throw; }
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
/*
 * 文件职责：MapStructureDebugOutput。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
