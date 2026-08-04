using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class FloorIndicatorClassification
{
    public bool Succeeded { get; init; }
    public string? Floor { get; init; }
    public double Confidence { get; init; }
    public double LocalizationConfidence { get; init; }
    public NormalizedRectangle? LocalizedRegion { get; init; }
    public double Contrast { get; init; }
    public double AnalysisMilliseconds { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

/// <summary>
/// Locates the 1/2 digit pair inside a coarse region, then classifies the
/// active button using relative luminance and texture.
/// </summary>
public sealed class FloorIndicatorRecognizer : IDisposable
{
    private readonly IndicatorProfile _firstFloorProfile;
    private readonly IndicatorProfile _secondFloorProfile;
    private readonly double _minimumTextureContrast;
    private readonly IReadOnlyList<DigitTemplate> _digitTemplates;
    private bool _disposed;

    public FloorIndicatorRecognizer(
        string firstFloorReferencePath,
        string secondFloorReferencePath)
    {
        _firstFloorProfile = ReadReferenceProfile(
            firstFloorReferencePath,
            new Point2d(90d, 68d),
            new Point2d(182d, 68d));
        _secondFloorProfile = ReadReferenceProfile(
            secondFloorReferencePath,
            new Point2d(120d, 83d),
            new Point2d(216d, 83d));
        if (!HasExpectedDirection(_firstFloorProfile, 1d)
            || !HasExpectedDirection(_secondFloorProfile, -1d))
        {
            throw new InvalidOperationException(
                "楼层参考素材无效：1F 必须激活左侧按钮，2F 必须激活右侧按钮。"
                + $" 实际纹理对比度 1F={_firstFloorProfile.TextureContrast:F3}，"
                + $"2F={_secondFloorProfile.TextureContrast:F3}。");
        }
        _minimumTextureContrast =
            MinimumReferenceMagnitude(profile => profile.TextureContrast)
            * FloorRecognitionRules.MinimumTextureContrastFactor;
        _digitTemplates =
        [
            ReadDigitTemplate(
                firstFloorReferencePath,
                digit: 2,
                new OpenCvSharp.Rect(170, 45, 25, 46)),
            ReadDigitTemplate(
                secondFloorReferencePath,
                digit: 1,
                new OpenCvSharp.Rect(108, 59, 24, 48))
        ];
    }

    public FloorIndicatorClassification Recognize(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride)
        => Recognize(
            bgraPixels,
            width,
            height,
            stride,
            new MapFloorRecognitionTuning());

    public FloorIndicatorClassification Recognize(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        MapFloorRecognitionTuning tuning)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning ??= new MapFloorRecognitionTuning();
        tuning = tuning.Clone();
        tuning.Normalize();
        var timer = Stopwatch.StartNew();
        if (width < 8
            || height < 8
            || stride < width * 4
            || bgraPixels.Length < stride * height)
        {
            timer.Stop();
            return new FloorIndicatorClassification
            {
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                FailureReason = "楼层显示区像素无效。"
            };
        }

        if (!TryLocateDigitPair(
                bgraPixels,
                width,
                height,
                stride,
                tuning,
                out var pair,
                out var localizationFailure))
        {
            timer.Stop();
            return new FloorIndicatorClassification
            {
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                FailureReason = localizationFailure
            };
        }

        var profile = CalculateProfile(
            bgraPixels,
            width,
            height,
            stride,
            pair.OneCenter,
            pair.TwoCenter);
        var contrast = profile.TextureContrast;
        if (!profile.IsFinite
            || Math.Abs(contrast) < _minimumTextureContrast)
        {
            timer.Stop();
            return new FloorIndicatorClassification
            {
                Contrast = contrast,
                LocalizationConfidence = pair.Confidence,
                LocalizedRegion = pair.Region,
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                FailureReason = "楼层按钮尚未稳定或左右状态差异不足。"
            };
        }

        // Mean brightness can change with the scene behind a translucent
        // control. Texture and edge energy are much harder for that background
        // to fake, so both must agree on the active side.
        var floor = contrast > 0d
            ? "1f"
            : "2f";
        var direction = floor == "1f" ? 1d : -1d;
        if (!HasExpectedDirection(profile, direction))
        {
            timer.Stop();
            return new FloorIndicatorClassification
            {
                Contrast = contrast,
                LocalizationConfidence = pair.Confidence,
                LocalizedRegion = pair.Region,
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                FailureReason = "楼层按钮状态与参考素材不一致。"
            };
        }

        var firstDistance = ProfileDistance(profile, _firstFloorProfile);
        var secondDistance = ProfileDistance(profile, _secondFloorProfile);
        var correctDistance = floor == "1f"
            ? firstDistance
            : secondDistance;
        var opposingDistance = floor == "1f"
            ? secondDistance
            : firstDistance;
        var directionConfidence = Math.Clamp(
            Math.Abs(contrast)
                / Math.Max(
                    _minimumTextureContrast * 4d,
                    FloorRecognitionRules.Epsilon),
            0d,
            1d);
        var profilePreference = Math.Clamp(
            0.5d
                + ((opposingDistance - correctDistance)
                    / Math.Max(
                        firstDistance + secondDistance,
                        FloorRecognitionRules.Epsilon)),
            0d,
            1d);
        var confidence = Math.Clamp(
            (pair.Confidence * 0.35d)
                + (directionConfidence * 0.35d)
                + (profilePreference * 0.30d),
            0d,
            1d);
        if (confidence < tuning.MinimumConfidence)
        {
            timer.Stop();
            return new FloorIndicatorClassification
            {
                Contrast = contrast,
                Confidence = confidence,
                LocalizationConfidence = pair.Confidence,
                LocalizedRegion = pair.Region,
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                FailureReason = "楼层按钮纹理与 1F/2F 参考素材差异过大。"
            };
        }
        timer.Stop();
        return new FloorIndicatorClassification
        {
            Succeeded = true,
            Floor = floor,
            Confidence = confidence,
            LocalizationConfidence = pair.Confidence,
            LocalizedRegion = pair.Region,
            Contrast = contrast,
            AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds
        };
    }

    public FloorIndicatorClassification Recognize(Mat image) =>
        Recognize(image, new MapFloorRecognitionTuning());

    public FloorIndicatorClassification Recognize(
        Mat image,
        MapFloorRecognitionTuning tuning)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty())
        {
            return new FloorIndicatorClassification
            {
                FailureReason = "楼层显示区图像为空。"
            };
        }

        using var bgra = new Mat();
        switch (image.Channels())
        {
            case 4:
                image.CopyTo(bgra);
                break;
            case 3:
                Cv2.CvtColor(image, bgra, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(image, bgra, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                return new FloorIndicatorClassification
                {
                    FailureReason = "楼层显示区使用了不支持的像素格式。"
                };
        }

        var stride = checked((int)bgra.Step());
        var pixels = new byte[checked(stride * bgra.Height)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return Recognize(
            pixels,
            bgra.Width,
            bgra.Height,
            stride,
            tuning);
    }

    private bool TryLocateDigitPair(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        MapFloorRecognitionTuning tuning,
        out LocatedDigitPair pair,
        out string failureReason)
    {
        pair = default;
        failureReason = string.Empty;
        var buffer = bgraPixels.ToArray();
        using var bgra = Mat.FromPixelData(
            height,
            width,
            MatType.CV_8UC4,
            buffer,
            stride);
        using var gray = new Mat();
        using var blurred = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(3, 3), 0d);
        Cv2.Canny(blurred, edges, 35d, 115d);

        var scales = new[] { 1d, 0.9d, 1.1d, 0.8d, 1.2d, 0.7d, 1.3d };
        var pairs = new List<PairCandidate>();
        foreach (var scale in scales)
        {
            var oneCandidates = FindDigitCandidates(
                blurred,
                edges,
                digit: 1,
                scale);
            var twoCandidates = FindDigitCandidates(
                blurred,
                edges,
                digit: 2,
                scale);
            var expectedSpacing = 94d * scale;
            var maximumVerticalDelta = Math.Max(8d, 14d * scale);
            foreach (var one in oneCandidates)
            {
                foreach (var two in twoCandidates)
                {
                    var spacing = two.Center.X - one.Center.X;
                    if (spacing <= 0d
                        || Math.Abs(spacing - expectedSpacing)
                            > expectedSpacing * 0.22d
                        || Math.Abs(two.Center.Y - one.Center.Y)
                            > maximumVerticalDelta)
                    {
                        continue;
                    }
                    var geometry = Math.Clamp(
                        1d
                        - (Math.Abs(spacing - expectedSpacing)
                            / (expectedSpacing * 0.22d))
                        - (Math.Abs(two.Center.Y - one.Center.Y)
                            / (maximumVerticalDelta * 2d)),
                        0d,
                        1d);
                    var confidence = Math.Clamp(
                        (Math.Min(one.Score, two.Score) * 0.55d)
                        + (((one.Score + two.Score) / 2d) * 0.25d)
                        + (geometry * 0.20d),
                        0d,
                        1d);
                    pairs.Add(new PairCandidate(
                        one,
                        two,
                        confidence));
                }
            }
            if (pairs.Any(candidate => candidate.Confidence >= 0.72d))
                break;
        }

        var winner = pairs
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();
        if (winner is null
            || winner.Confidence < tuning.MinimumLocalizationConfidence)
        {
            failureReason =
                "在粗校准区域内没有定位到可信且成对的楼层数字 1/2。";
            return false;
        }

        var oneCenter = winner.One.Center;
        var twoCenter = winner.Two.Center;
        var canonicalScale = Math.Max(
            0.2d,
            (twoCenter.X - oneCenter.X) / 94d);
        var left = oneCenter.X - (90d * canonicalScale);
        var top = ((oneCenter.Y + twoCenter.Y) / 2d)
            - (68d * canonicalScale);
        var right = left + (270d * canonicalScale);
        var bottom = top + (137d * canonicalScale);
        var normalizedLeft = Math.Clamp(left / width, 0d, 1d);
        var normalizedTop = Math.Clamp(top / height, 0d, 1d);
        var normalizedRight = Math.Clamp(right / width, normalizedLeft, 1d);
        var normalizedBottom = Math.Clamp(
            bottom / height,
            normalizedTop,
            1d);
        var region = new NormalizedRectangle
        {
            X = normalizedLeft,
            Y = normalizedTop,
            Width = normalizedRight - normalizedLeft,
            Height = normalizedBottom - normalizedTop
        };
        if (!region.IsValid)
        {
            failureReason = "楼层数字已找到，但双按钮区域超出粗校准范围。";
            return false;
        }

        pair = new LocatedDigitPair(
            oneCenter,
            twoCenter,
            winner.Confidence,
            region);
        return true;
    }

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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var template in _digitTemplates)
        {
            template.Gray.Dispose();
            template.Edges.Dispose();
        }
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
