using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Tracks one already-selected map without performing catalog-wide identity
/// ranking. All degraded tracking uses the scale locked by a prior gate pair.
/// </summary>
public static partial class MapAnchorTracker
{

    private static bool TryMatchTemplate(
        Mat image,
        Mat template,
        Rect domain,
        double requiredScore,
        double requiredAdvantage,
        out double bestScore,
        out double secondScore,
        out Point bestLocation)
    {
        bestScore = 0d;
        secondScore = 0d;
        bestLocation = default;
        if (domain.Width <= 0
            || domain.Height <= 0
            || domain.Right + template.Width - 1 > image.Width
            || domain.Bottom + template.Height - 1 > image.Height)
        {
            return false;
        }

        using var source = new Mat(
            image,
            new Rect(
                domain.X,
                domain.Y,
                domain.Width + template.Width - 1,
                domain.Height + template.Height - 1));
        using var scores = new Mat();
        Cv2.MatchTemplate(
            source,
            template,
            scores,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(
            scores,
            out _,
            out bestScore,
            out _,
            out var localBest);
        bestLocation = new Point(
            domain.X + localBest.X,
            domain.Y + localBest.Y);
        using var suppressed = scores.Clone();
        Cv2.Rectangle(
            suppressed,
            CreateSuppressionRect(
                localBest,
                template.Size(),
                suppressed.Size()),
            Scalar.All(-1d),
            -1);
        Cv2.MinMaxLoc(
            suppressed,
            out _,
            out secondScore,
            out _,
            out _);
        return bestScore >= requiredScore
            && bestScore - secondScore >= requiredAdvantage;
    }

    private static MapScreenRect ToReferenceBounds(
        NormalizedRectangle bounds,
        int width,
        int height) =>
        new(
            bounds.X * width,
            bounds.Y * height,
            bounds.Width * width,
            bounds.Height * height);

    private static Rect ToClampedRect(
        MapScreenRect bounds,
        int imageWidth,
        int imageHeight)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling(bounds.X + bounds.Width),
            left + 1,
            imageWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(bounds.Y + bounds.Height),
            top + 1,
            imageHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool TryExtractCenteredPatch(
        Mat image,
        double centerX,
        double centerY,
        int width,
        int height,
        out Mat patch)
    {
        patch = new Mat();
        if (width < MinimumTemplatePixels
            || height < MinimumTemplatePixels
            || width > image.Width
            || height > image.Height)
        {
            return false;
        }
        var left = (int)Math.Round(centerX - (width / 2d));
        var top = (int)Math.Round(centerY - (height / 2d));
        left = Math.Clamp(left, 0, image.Width - width);
        top = Math.Clamp(top, 0, image.Height - height);
        patch = new Mat(image, new Rect(left, top, width, height)).Clone();
        return true;
    }

    private static Rect CreateSuppressionRect(Point location, Size template, Size output)
    {
        var left = Math.Max(0, location.X - (template.Width / 2));
        var top = Math.Max(0, location.Y - (template.Height / 2));
        var right = Math.Min(output.Width, location.X + template.Width);
        var bottom = Math.Min(output.Height, location.Y + template.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double CosineSimilarity(Mat left, Mat right)
    {
        using var leftFloat = new Mat();
        using var rightFloat = new Mat();
        left.ConvertTo(leftFloat, MatType.CV_32FC1);
        right.ConvertTo(rightFloat, MatType.CV_32FC1);
        var denominator = Cv2.Norm(leftFloat) * Cv2.Norm(rightFloat);
        return denominator <= 0.000001d
            ? 0d
            : Math.Clamp(leftFloat.Dot(rightFloat) / denominator, 0d, 1d);
    }

    private static double NormalizeThreshold(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : fallback;

    private static bool HasPreliminaryIndependentConsensus(
        IReadOnlyList<OffsetCandidate> candidates,
        MapGeometryFingerprint fingerprint,
        MapScreenRect viewportBounds)
    {
        if (candidates.Count < 2)
            return false;
        var offsetTolerance = Math.Max(
            MinimumConsensusPixels,
            Math.Sqrt(
                (viewportBounds.Width * viewportBounds.Width)
                + (viewportBounds.Height * viewportBounds.Height))
            * ConsensusViewportRatio);
        var referenceDistance = Math.Sqrt(
            (fingerprint.ReferenceWidth * fingerprint.ReferenceWidth)
            + (fingerprint.ReferenceHeight * fingerprint.ReferenceHeight))
            * 0.05d;
        for (var leftIndex = 0;
             leftIndex < candidates.Count - 1;
             leftIndex++)
        {
            for (var rightIndex = leftIndex + 1;
                 rightIndex < candidates.Count;
                 rightIndex++)
            {
                var left = candidates[leftIndex];
                var right = candidates[rightIndex];
                if (Distance(
                        left.OffsetX,
                        left.OffsetY,
                        right.OffsetX,
                        right.OffsetY) <= offsetTolerance
                    && Distance(
                        left.Evidence.ReferenceBounds.CenterX,
                        left.Evidence.ReferenceBounds.CenterY,
                        right.Evidence.ReferenceBounds.CenterX,
                        right.Evidence.ReferenceBounds.CenterY)
                        >= referenceDistance)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static double Distance(
        double leftX,
        double leftY,
        double rightX,
        double rightY)
    {
        var deltaX = rightX - leftX;
        var deltaY = rightY - leftY;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }
}
