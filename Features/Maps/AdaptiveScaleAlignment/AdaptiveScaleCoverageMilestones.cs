using OpenCvSharp;

namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

/// <summary>Monotonic floor-local milestones. A failed refresh never consumes the pending request.</summary>
internal sealed class AdaptiveScaleCoverageMilestones
{
    // Milestones only ever advance.  Individual frame coverage may fall when
    // the player moves into a corner, but that must neither roll this state
    // back nor schedule an already-consumed refresh again.
    private static readonly int[] MilestonePercents = [10, 15, 20, 25, 30, 40, 50];

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
        var coveragePercent = coverage * 100d;
        var reached = 0;
        foreach (var milestone in MilestonePercents)
        {
            if (coveragePercent + 1e-9 < milestone)
                break;
            reached = milestone;
        }
        if (reached <= ReachedPercent)
            return;
        ReachedPercent = reached;
        RefreshPending = true;
    }
}
