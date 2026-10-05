using OpenCvSharp;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>一次命中的结果：相似度 + 模板在帧内的矩形。</summary>
internal readonly record struct MatchHit(double Score, Rect Box, double Scale)
{
    public double CenterX => Box.X + (Box.Width / 2d);

    public double CenterY => Box.Y + (Box.Height / 2d);
}

/// <summary>
/// 多尺度 + 限定区域的模板匹配。基础缩放按「当前帧宽度 / 1920」推算，
/// 再在其上下各留两档（0.90–1.10），以同时适应窗口化与非 1080p 分辨率。
/// </summary>
internal sealed class SceneQuickActionsMatcher : ISceneQuickActionsMatcher, IDisposable
{
    private static readonly double[] ScaleFactors = [0.90, 0.95, 1.00, 1.05, 1.10];

    private readonly Dictionary<string, Mat> _templates = new(StringComparer.Ordinal);
    private bool _disposed;

    public SceneQuickActionsMatcher()
    {
        foreach (var name in SceneQuickActionsTemplates.All)
            _templates[name] = SceneQuickActionsTemplates.LoadGray(name);
    }

    public bool TryMatch(Mat grayFrame, string templateName, double threshold, out MatchHit hit)
    {
        hit = default;
        if (_disposed
            || grayFrame.Empty()
            || !_templates.TryGetValue(templateName, out var template)
            || template.Empty())
        {
            return false;
        }

        var region = SceneQuickActionsTemplates.GetSearchRegion(templateName, grayFrame.Size());
        if (region.Width < 16 || region.Height < 16
            || region.Width > grayFrame.Width || region.Height > grayFrame.Height
            || region.X + region.Width > grayFrame.Width
            || region.Y + region.Height > grayFrame.Height)
        {
            return false;
        }

        using var searchArea = new Mat(grayFrame, region);
        var baseScale = grayFrame.Width / (double)SceneQuickActionsTemplates.ReferenceWidth;
        var bestScore = double.NegativeInfinity;
        var bestLocation = new Point();
        var bestSize = new Size(template.Width, template.Height);
        var bestScale = baseScale;

        foreach (var factor in ScaleFactors)
        {
            var scale = baseScale * factor;
            var width = (int)Math.Round(template.Width * scale);
            var height = (int)Math.Round(template.Height * scale);
            if (width < 8 || height < 8 || width >= searchArea.Width || height >= searchArea.Height)
                continue;

            using var scaled = new Mat();
            Cv2.Resize(
                template,
                scaled,
                new Size(width, height),
                0,
                0,
                width < template.Width ? InterpolationFlags.Area : InterpolationFlags.Linear);

            using var result = new Mat();
            Cv2.MatchTemplate(searchArea, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out var maxLocation);
            if (maxValue > bestScore)
            {
                bestScore = maxValue;
                bestLocation = maxLocation;
                bestSize = new Size(width, height);
                bestScale = scale;
            }
        }

        if (!double.IsFinite(bestScore) || bestScore < threshold)
            return false;

        hit = new MatchHit(
            bestScore,
            new Rect(region.X + bestLocation.X, region.Y + bestLocation.Y, bestSize.Width, bestSize.Height),
            bestScale);
        return true;
    }

    /// <summary>把帧内坐标换算成屏幕绝对坐标（帧即客户区，1:1 物理像素）。</summary>
    public static (int X, int Y) ToScreen(
        double frameX,
        double frameY,
        IDVBuff.PluginContracts.PluginClientBounds bounds,
        Size frameSize)
    {
        var scaleX = frameSize.Width > 0 ? bounds.Width / (double)frameSize.Width : 1d;
        var scaleY = frameSize.Height > 0 ? bounds.Height / (double)frameSize.Height : 1d;
        return (
            bounds.X + (int)Math.Round(frameX * scaleX),
            bounds.Y + (int)Math.Round(frameY * scaleY));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var template in _templates.Values)
            template.Dispose();
        _templates.Clear();
    }
}
