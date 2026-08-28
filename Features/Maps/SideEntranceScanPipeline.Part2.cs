using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>
/// 侧门专属扫描管线：对捕获帧运行模板匹配，返回 TopK 候选地图。
/// 与双门管线并列，仅用于首次地图识别，对齐阶段仍由原有管线处理。
/// </summary>
public sealed partial class SideEntranceScanPipeline
{

    /// <summary>
    /// 按给定缩放重采样模板后在 <paramref name="searchRegion"/> 内匹配一次。
    /// 模板放大用 <see cref="InterpolationFlags.Cubic"/>、缩小用
    /// <see cref="InterpolationFlags.Area"/>，避免缩小时的锯齿压低相关性得分。
    /// </summary>
    /// <param name="searchRegion">搜索区域（细化窗口，全分辨率）。</param>
    /// <param name="regionOrigin">
    ///   搜索区域左上角在完整帧中的坐标；匹配位置会加回该原点，
    ///   使返回的 MatchLocation 始终是帧坐标而非窗口内的相对坐标。
    /// </param>
    private static SideEntranceScanCandidate? Evaluate(
        Mat searchRegion,
        Point regionOrigin,
        Mat template,
        MapRecord map,
        string floorKey,
        double scale)
    {
        var width = (int)Math.Round(template.Width * scale);
        var height = (int)Math.Round(template.Height * scale);
        // 模板必须严格小于搜索图：等大时 MatchTemplate 只会产出 1×1 的平凡结果。
        if (width < 8 || height < 8
            || width >= searchRegion.Width || height >= searchRegion.Height)
        {
            return null;
        }

        using var scaled = new Mat();
        Cv2.Resize(
            template,
            scaled,
            new Size(width, height),
            0d,
            0d,
            scale >= 1d ? InterpolationFlags.Cubic : InterpolationFlags.Area);

        using var resultMat = new Mat();
        Cv2.MatchTemplate(
            searchRegion,
            scaled,
            resultMat,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(resultMat, out _, out var maxVal, out _, out var maxLoc);
        if (!double.IsFinite(maxVal))
            return null;

        return new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = floorKey,
            MatchScore = maxVal,
            MatchScale = scale,
            MatchLocation = new MapScreenRect(
                regionOrigin.X + maxLoc.X,
                regionOrigin.Y + maxLoc.Y,
                width,
                height)
        };
    }
}
