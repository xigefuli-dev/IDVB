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
public sealed partial class FloorIndicatorRecognizer : IDisposable
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
}
