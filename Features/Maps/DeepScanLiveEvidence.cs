using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Evidence-space exclusions for DeepScan. Never alters shared extraction or reference caches.</summary>
internal static class DeepScanLiveEvidence
{
    internal static void MaskAnnotations(Mat source, Vpsg3LiveObservation observation)
    {
        if (source.Channels() is not (3 or 4)) return;
        using var bgr = new Mat();
        if (source.Channels() == 4) Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        else source.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        // Compact player/door markers, enemies and purple chest overlays. Their
        // colored interiors provide the measured footprint; only a small rim is
        // unknown, not a player-sized expansion into the surrounding walls.
        foreach (var (low, high, rim) in new[]
        {
            (new Scalar(10, 120, 170), new Scalar(90, 255, 255), 4),
            (new Scalar(0, 75, 120), new Scalar(8, 255, 255), 8),
            (new Scalar(172, 75, 120), new Scalar(179, 255, 255), 8),
            (new Scalar(118, 20, 145), new Scalar(140, 130, 255), 4)
        })
        {
            using var mask = new Mat();
            Cv2.InRange(hsv, low, high, mask);
            if (low.Val0 == 118)
            {
                // Chest highlights are disconnected bars around a dark interior.
                // Join that footprint before measuring compactness; a single thin
                // wall highlight still has no two-dimensional interior.
                using var join = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, join);
            }
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
            for (var i = 1; i < count; i++)
            {
                var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                var width = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                var height = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                if (area < 6 || width > 64 || height > 64) continue;
                // Blue-purple wall highlights can share the chest hue. A chest
                // must occupy a compact two-dimensional footprint, not a thin rim.
                if (low.Val0 == 118 && (width < 8 || height < 7
                    || width > height * 3 || height > width * 3 || area < width * height * .18)) continue;
                var rect = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left) - rim,
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Top) - rim,
                    width + rim * 2, height + rim * 2).Intersect(new(0, 0, source.Width, source.Height));
                Cv2.Rectangle(observation.ObservedEdges, rect, Scalar.Black, -1);
                Cv2.Rectangle(observation.ProposalEdges, rect, Scalar.Black, -1);
                Cv2.Rectangle(observation.ValidMask, rect, Scalar.Black, -1);
            }
        }
    }
}
