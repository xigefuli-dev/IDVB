using OpenCvSharp;

namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

/// <summary>Monotonic floor-local milestones. A failed refresh never consumes the pending request.</summary>
internal sealed class AdaptiveScaleCoverageMilestones
{
    internal static int CountCoveredPoints(IReadOnlyList<Point> referencePoints, Mat observedEdges,
        MapOverlayTransform transform, MapScreenRect viewport)
    {
        using var supported = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(observedEdges, supported, kernel);
        var hits = 0;
        foreach (var point in referencePoints)
        {
            // Prebuilt contours already use the canonical floor orientation.
            var x = (int)Math.Round(point.X * transform.ScaleX + transform.OffsetX - viewport.X);
            var y = (int)Math.Round(point.Y * transform.ScaleY + transform.OffsetY - viewport.Y);
            if ((uint)x < supported.Width && (uint)y < supported.Height && supported.At<byte>(y, x) != 0)
                hits++;
        }
        return hits;
    }

    public int ReachedPercent { get; private set; }
    public bool RefreshPending { get; private set; }

    public void Observe(double coverage, bool refreshed)
    {
        if (refreshed)
            RefreshPending = false;
        if (!double.IsFinite(coverage) || coverage < 0d || coverage > 1d)
            return;
        var reached = Math.Min(5, (int)Math.Floor(coverage * 10d + 1e-9)) * 10;
        if (reached <= ReachedPercent)
            return;
        ReachedPercent = reached;
        RefreshPending = true;
    }
}
