using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// VPSG 3.0 诊断捕获服务。
/// 直接复用对齐阶段已提取的实时结构线（observation.ObservedEdges），
/// 仅在诊断模式开启时按需生成参考预制线的仿射投影贴合重叠图。
/// </summary>
internal static class Vpsg3DiagnosticCapture
{
    /// <summary>
    /// 若诊断模式处于激活状态，输出结构配准图（直接复用）与贴合重叠图（按需生成）。
    /// </summary>
    internal static void CaptureIfActive(
        int? attemptId,
        Vpsg3LiveObservation observation,
        string? referencePath,
        Vpsg3BootstrapResult result,
        string? tag = null)
    {
        if (!MapDiagnosticModeCapture.IsActive || observation is null || result is null)
            return;

        try
        {
            // 1. 结构配准图：直接复用对齐时提取出的 ObservedEdges，严禁重新提取
            MapDiagnosticModeCapture.WriteInputs(
                observation.ObservedEdges,
                observation.ObservedEdges,
                attemptId,
                tag);

            // 2. 贴合重叠图：仅在诊断模式开启时按需生成仿射变换图
            using var overlay = CreateFittedOverlay(observation, referencePath, result);
            if (overlay is not null)
            {
                MapDiagnosticModeCapture.WriteFitness(overlay, attemptId, tag);
            }
        }
        catch
        {
            // 诊断捕获决不能影响对齐结果或抛出未捕获异常
        }
    }

    /// <summary>
    /// 按需生成贴合重叠图：将参考预制线投影至视口空间，
    /// 绿色=实时结构线，红色=投影参考线，黄色=贴合重合部分。
    /// </summary>
    internal static Mat? CreateFittedOverlay(
        Vpsg3LiveObservation observation,
        string? referencePath,
        Vpsg3BootstrapResult result)
    {
        if (observation.ObservedEdges.Empty())
            return null;

        var size = observation.ObservedEdges.Size();

        if (string.IsNullOrEmpty(referencePath) || !File.Exists(referencePath))
        {
            var missingVisual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
            missingVisual.SetTo(new Scalar(0, 170, 0), observation.ObservedEdges);
            Cv2.PutText(
                missingVisual,
                $"VPSG3: Reference line missing ({result.FallbackReason})",
                new Point(10, 25),
                HersheyFonts.HersheySimplex,
                0.6,
                Scalar.OrangeRed,
                2);
            return missingVisual;
        }

        try
        {
            using var referenceEdges = Cv2.ImRead(referencePath, ImreadModes.Grayscale);
            if (referenceEdges.Empty())
                return null;

            if (result.ScaleResult.Success)
            {
                var scale = result.Scale;
                var tx = result.OffsetX - observation.ViewportBounds.X;
                var ty = result.OffsetY - observation.ViewportBounds.Y;

                using var matrix = new Mat(2, 3, MatType.CV_64FC1);
                matrix.Set(0, 0, scale);
                matrix.Set(0, 1, 0d);
                matrix.Set(0, 2, tx);
                matrix.Set(1, 0, 0d);
                matrix.Set(1, 1, scale);
                matrix.Set(1, 2, ty);

                using var projected = new Mat();
                Cv2.WarpAffine(
                    referenceEdges,
                    projected,
                    matrix,
                    size,
                    InterpolationFlags.Area,
                    BorderTypes.Constant,
                    Scalar.Black);
                Cv2.Threshold(projected, projected, 127d, 255d, ThresholdTypes.Binary);

                var visual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
                visual.SetTo(new Scalar(0, 170, 0), observation.ObservedEdges); // 实时边缘：绿
                visual.SetTo(new Scalar(0, 0, 220), projected);                // 投影参考：红
                using var overlap = new Mat();
                Cv2.BitwiseAnd(observation.ObservedEdges, projected, overlap);
                visual.SetTo(new Scalar(0, 255, 255), overlap);                 // 重叠区域：黄

                var statusText = result.IsAccepted
                    ? $"VPSG3 Accepted: s={result.Scale:F4} m={result.ApertureMargin:F3} c={result.Confidence:P0}"
                    : $"VPSG3 {(result.ScaleResult.Success ? "Rejected" : "ScaleFailed")}: {result.FallbackReason}";

                Cv2.PutText(
                    visual,
                    statusText,
                    new Point(10, 25),
                    HersheyFonts.HersheySimplex,
                    0.6,
                    result.IsAccepted ? Scalar.LimeGreen : Scalar.OrangeRed,
                    2);

                return visual;
            }
            else
            {
                var visual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
                visual.SetTo(new Scalar(0, 170, 0), observation.ObservedEdges);
                Cv2.PutText(
                    visual,
                    $"VPSG3 Scale Failed: {result.ScaleResult.RejectReason}",
                    new Point(10, 25),
                    HersheyFonts.HersheySimplex,
                    0.6,
                    Scalar.OrangeRed,
                    2);
                return visual;
            }
        }
        catch
        {
            return null;
        }
    }
}
