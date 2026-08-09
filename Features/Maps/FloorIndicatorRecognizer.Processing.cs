// IDVB Remaster — FloorIndicatorRecognizer 图像处理辅助方法

using OpenCvSharp;
using System.Drawing;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public sealed partial class FloorIndicatorRecognizer
{
    private IReadOnlyList<DigitCandidate> FindDigitCandidates(
        Mat liveGray,
        Mat liveEdges,
        int digit,
        double scale)
    {
        var candidates = new List<DigitCandidate>();
        foreach (var source in _digitTemplates.Where(template =>
                     template.Digit == digit))
        {
            var targetWidth = Math.Max(
                8,
                (int)Math.Round(source.Edges.Width * scale));
            var targetHeight = Math.Max(
                12,
                (int)Math.Round(source.Edges.Height * scale));
            if (targetWidth >= liveEdges.Width
                || targetHeight >= liveEdges.Height)
            {
                continue;
            }
            using var edgeTemplate = new Mat();
            Cv2.Resize(
                source.Edges,
                edgeTemplate,
                new OpenCvSharp.Size(targetWidth, targetHeight),
                0d,
                0d,
                InterpolationFlags.Area);
            using var grayTemplate = new Mat();
            Cv2.Resize(
                source.Gray,
                grayTemplate,
                new OpenCvSharp.Size(targetWidth, targetHeight),
                0d,
                0d,
                InterpolationFlags.Area);
            using var edgeScores = new Mat();
            Cv2.MatchTemplate(
                liveEdges,
                edgeTemplate,
                edgeScores,
                TemplateMatchModes.CCoeffNormed);
            CollectDigitCandidates(
                edgeScores,
                targetWidth,
                targetHeight,
                candidates);
            using var grayScores = new Mat();
            Cv2.MatchTemplate(
                liveGray,
                grayTemplate,
                grayScores,
                TemplateMatchModes.CCoeffNormed);
            CollectDigitCandidates(
                grayScores,
                targetWidth,
                targetHeight,
                candidates);
        }
        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .DistinctBy(candidate => (
                (int)Math.Round(candidate.Center.X / 4d),
                (int)Math.Round(candidate.Center.Y / 4d)))
            .Take(8)
            .ToArray();
    }

    private static void CollectDigitCandidates(
        Mat scores,
        int targetWidth,
        int targetHeight,
        ICollection<DigitCandidate> candidates)
    {
            for (var index = 0; index < 3; index++)
            {
                Cv2.MinMaxLoc(
                    scores,
                    out _,
                    out var score,
                    out _,
                    out var location);
                if (score < 0.28d)
                    break;
                candidates.Add(new DigitCandidate(
                    new Point2d(
                        location.X + (targetWidth / 2d),
                        location.Y + (targetHeight / 2d)),
                    score));
                var suppressionLeft = Math.Max(
                    0,
                    location.X - targetWidth);
                var suppressionTop = Math.Max(
                    0,
                    location.Y - targetHeight);
                var suppressionRight = Math.Min(
                    scores.Width,
                    location.X + targetWidth + 1);
                var suppressionBottom = Math.Min(
                    scores.Height,
                    location.Y + targetHeight + 1);
                Cv2.Rectangle(
                    scores,
                    new OpenCvSharp.Rect(
                        suppressionLeft,
                        suppressionTop,
                        suppressionRight - suppressionLeft,
                        suppressionBottom - suppressionTop),
                    Scalar.All(-1d),
                    -1);
            }
    }

    private static DigitTemplate ReadDigitTemplate(
        string path,
        int digit,
        OpenCvSharp.Rect crop)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (image.Empty()
            || crop.X < 0
            || crop.Y < 0
            || crop.Right > image.Width
            || crop.Bottom > image.Height)
        {
            throw new InvalidOperationException(
                $"楼层数字 {digit} 的参考区域无效。");
        }
        using var patch = new Mat(image, crop);
        using var blurred = new Mat();
        var gray = new Mat();
        var edges = new Mat();
        Cv2.GaussianBlur(patch, blurred, new OpenCvSharp.Size(3, 3), 0d);
        blurred.CopyTo(gray);
        Cv2.Canny(
            blurred,
            edges,
            FloorRecognitionRules.CannyLowThreshold,
            FloorRecognitionRules.CannyHighThreshold);
        return new DigitTemplate(digit, gray, edges);
    }

    private double MinimumReferenceMagnitude(
        Func<IndicatorProfile, double> selector) =>
        Math.Min(
            Math.Abs(selector(_firstFloorProfile)),
            Math.Abs(selector(_secondFloorProfile)));

    private static bool HasExpectedDirection(
        IndicatorProfile profile,
        double direction) =>
        (profile.TextureContrast * direction) > 0d
        && (profile.DeviationContrast * direction) > 0d
        && (profile.GradientContrast * direction) > 0d;

    private static double ProfileDistance(
        IndicatorProfile left,
        IndicatorProfile right) =>
        (FloorRecognitionRules.MeanContrastWeight
            * Math.Abs(left.MeanContrast - right.MeanContrast))
        + (FloorRecognitionRules.DeviationContrastWeight * Math.Abs(
            left.DeviationContrast - right.DeviationContrast))
        + (FloorRecognitionRules.GradientContrastWeight * Math.Abs(
            left.GradientContrast - right.GradientContrast));

    private static IndicatorProfile ReadReferenceProfile(
        string path,
        Point2d oneCenter,
        Point2d twoCenter)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到楼层参考素材。", path);
        using var bitmap = new Bitmap(path);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var index = ((y * bitmap.Width) + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = color.A;
            }
        }
        return CalculateProfile(
            pixels,
            bitmap.Width,
            bitmap.Height,
            bitmap.Width * 4,
            oneCenter,
            twoCenter);
    }

    private static IndicatorProfile CalculateProfile(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        Point2d oneCenter,
        Point2d twoCenter)
    {
        var digitSpacing = Math.Max(
            8d,
            Math.Abs(twoCenter.X - oneCenter.X));
        var halfWidth = digitSpacing * 0.43d;
        var halfHeight = digitSpacing * 0.42d;
        var leftActivity = CalculateActivity(
            pixels,
            width,
            height,
            stride,
            Math.Clamp(
                (int)Math.Round(oneCenter.X - halfWidth),
                0,
                width - 1),
            Math.Clamp(
                (int)Math.Round(oneCenter.X + halfWidth),
                1,
                width),
            Math.Clamp(
                (int)Math.Round(oneCenter.Y - halfHeight),
                0,
                height - 1),
            Math.Clamp(
                (int)Math.Round(oneCenter.Y + halfHeight),
                1,
                height));
        var rightActivity = CalculateActivity(
            pixels,
            width,
            height,
            stride,
            Math.Clamp(
                (int)Math.Round(twoCenter.X - halfWidth),
                0,
                width - 1),
            Math.Clamp(
                (int)Math.Round(twoCenter.X + halfWidth),
                1,
                width),
            Math.Clamp(
                (int)Math.Round(twoCenter.Y - halfHeight),
                0,
                height - 1),
            Math.Clamp(
                (int)Math.Round(twoCenter.Y + halfHeight),
                1,
                height));
        var meanContrast = NormalizeDifference(
            leftActivity.Mean,
            rightActivity.Mean);
        var deviationContrast = NormalizeDifference(
            leftActivity.StandardDeviation,
            rightActivity.StandardDeviation);
        var gradientContrast = NormalizeDifference(
            leftActivity.MeanGradient,
            rightActivity.MeanGradient);
        return new IndicatorProfile(
            meanContrast,
            deviationContrast,
            gradientContrast);
    }

    private static double NormalizeDifference(double left, double right) =>
        (left - right) / Math.Max(
            Math.Max(left, right),
            FloorRecognitionRules.Epsilon);

    private static ActivityProfile CalculateActivity(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        int left,
        int right,
        int top,
        int bottom)
    {
        if (right <= left || bottom <= top)
            return default;
        var zonePixels = (right - left) * (bottom - top);
        var step = Math.Max(1, (int)Math.Sqrt(zonePixels / 4096d));
        double sum = 0d;
        double squaredSum = 0d;
        double gradientSum = 0d;
        var count = 0;

        for (var y = top; y < bottom; y += step)
        {
            for (var x = left; x < right; x += step)
            {
                var luminance = ReadLuminance(pixels, stride, x, y);
                sum += luminance;
                squaredSum += luminance * luminance;
                if (x + step < right)
                {
                    gradientSum += Math.Abs(
                        luminance - ReadLuminance(pixels, stride, x + step, y));
                }
                if (y + step < bottom)
                {
                    gradientSum += Math.Abs(
                        luminance - ReadLuminance(pixels, stride, x, y + step));
                }
                count++;
            }
        }

        if (count == 0)
            return default;
        var mean = sum / count;
        var variance = Math.Max(0d, (squaredSum / count) - (mean * mean));
        var standardDeviation = Math.Sqrt(variance);
        var meanGradient = gradientSum / (count * 2d);
        return new ActivityProfile(mean, standardDeviation, meanGradient);
    }

    private static double ReadLuminance(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x,
        int y)
    {
        var index = (y * stride) + (x * 4);
        return ((pixels[index + 2] * 54d)
            + (pixels[index + 1] * 183d)
            + (pixels[index] * 19d))
            / (256d * 255d);
    }

    private sealed record DigitTemplate(
        int Digit,
        Mat Gray,
        Mat Edges);

    private sealed record DigitCandidate(
        Point2d Center,
        double Score);

    private sealed record PairCandidate(
        DigitCandidate One,
        DigitCandidate Two,
        double Confidence);

    private readonly record struct LocatedDigitPair(
        Point2d OneCenter,
        Point2d TwoCenter,
        double Confidence,
        NormalizedRectangle Region);

    private readonly record struct ActivityProfile(
        double Mean,
        double StandardDeviation,
        double MeanGradient);

    private readonly record struct IndicatorProfile(
        double MeanContrast,
        double DeviationContrast,
        double GradientContrast)
    {
        public double TextureContrast =>
            (0.65d * DeviationContrast) + (0.35d * GradientContrast);

        public bool IsFinite =>
            double.IsFinite(MeanContrast)
            && double.IsFinite(DeviationContrast)
            && double.IsFinite(GradientContrast);
    }
}
