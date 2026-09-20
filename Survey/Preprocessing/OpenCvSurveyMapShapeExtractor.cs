using IDVBuff.Survey.Contracts;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace IDVBuff.Survey.Preprocessing.OpenCv;

internal sealed class OpenCvSurveyMapShapeExtractor
{
    private readonly SurveyPreprocessingTuning _tuning;

    public OpenCvSurveyMapShapeExtractor(SurveyPreprocessingTuning tuning) => _tuning = tuning;

    public Mat Extract(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty())
            throw new ArgumentException("Map shape extraction requires an image.", nameof(image));

        using var hsv = new Mat();
        using var lab = new Mat();
        Cv2.CvtColor(image, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(image, lab, ColorConversionCodes.BGR2Lab);
        var hsvChannels = Cv2.Split(hsv);
        var labChannels = Cv2.Split(lab);
        try
        {
            var bounds = EstimateAdaptiveBounds(
                hsv,
                hsvChannels[2],
                labChannels[0]);
            using var seed = CreateColorSeed(hsv, labChannels, bounds);
            RestrictToMapCanvas(seed);

            var minimumDimension = Math.Min(image.Width, image.Height);
            var openingRadius = Math.Max(
                2,
                (int)Math.Round(minimumDimension / _tuning.ShapeOpeningDivisor));
            var closingRadius = Math.Max(
                2,
                (int)Math.Round(minimumDimension / _tuning.ShapeClosingDivisor));
            using var openingKernel = CreateKernel(openingRadius);
            using var closingKernel = CreateKernel(closingRadius);
            Cv2.MorphologyEx(seed, seed, MorphTypes.Open, openingKernel);
            Cv2.MorphologyEx(seed, seed, MorphTypes.Close, closingKernel);

            var minimumArea = Math.Max(
                192,
                (int)Math.Round(image.Width * image.Height
                    * _tuning.MinimumShapeComponentAreaRatio));
            var minimumRadius = Math.Max(
                1.5d,
                openingRadius * _tuning.MinimumShapeThicknessFactor);
            using var filtered = FilterComponents(seed, minimumArea, minimumRadius);
            var maximumHoleArea = Math.Max(
                64,
                (int)Math.Round(image.Width * image.Height
                    * _tuning.MaximumShapeHoleAreaRatio));
            using var filled = FillSmallHoles(filtered, maximumHoleArea);
            using var finalClosingKernel = CreateKernel(Math.Max(1, closingRadius / 2));
            Cv2.MorphologyEx(
                filled,
                filled,
                MorphTypes.Close,
                finalClosingKernel);
            return FilterComponents(filled, minimumArea, minimumRadius);
        }
        finally
        {
            DisposeAll(hsvChannels);
            DisposeAll(labChannels);
        }
    }

    private static AdaptiveBounds EstimateAdaptiveBounds(Mat hsv, Mat value, Mat lightness)
    {
        using var blueFamily = new Mat();
        using var lowBrownFamily = new Mat();
        using var highBrownFamily = new Mat();
        using var neutralFamily = new Mat();
        using var sample = new Mat();
        Cv2.InRange(hsv, new Scalar(86, 8, 0), new Scalar(135, 125, 255), blueFamily);
        Cv2.InRange(hsv, new Scalar(0, 10, 0), new Scalar(38, 135, 255), lowBrownFamily);
        Cv2.InRange(hsv, new Scalar(172, 10, 0), new Scalar(179, 135, 255), highBrownFamily);
        Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(179, 33, 255), neutralFamily);
        Cv2.BitwiseOr(blueFamily, lowBrownFamily, sample);
        Cv2.BitwiseOr(sample, highBrownFamily, sample);
        Cv2.BitwiseOr(sample, neutralFamily, sample);
        using var valueRange = new Mat();
        using var lightnessRange = new Mat();
        Cv2.InRange(value, new Scalar(48), new Scalar(220), valueRange);
        Cv2.Threshold(lightness, lightnessRange, 43d, 255d, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(sample, valueRange, sample);
        Cv2.BitwiseAnd(sample, lightnessRange, sample);
        var count = Cv2.CountNonZero(sample);
        if (count < 500)
            return new AdaptiveBounds(68, 190, 52, 208);

        var valueHistogram = BuildHistogram(value, sample);
        var lightnessHistogram = BuildHistogram(lightness, sample);
        return new AdaptiveBounds(
            Math.Clamp(Percentile(valueHistogram, count, 0.04d) - 5, 58, 76),
            Math.Clamp(Percentile(valueHistogram, count, 0.98d) + 5, 175, 205),
            Math.Clamp(Percentile(lightnessHistogram, count, 0.03d) - 5, 48, 62),
            Math.Clamp(Percentile(lightnessHistogram, count, 0.99d) + 5, 195, 218));
    }

    private static Mat CreateColorSeed(Mat hsv, IReadOnlyList<Mat> labChannels, AdaptiveBounds bounds)
    {
        using var grayBlue = new Mat();
        using var lowBrown = new Mat();
        using var highBrown = new Mat();
        using var neutral = new Mat();
        var seed = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(88, 12, bounds.ValueLow),
            new Scalar(132, 84, bounds.ValueHigh),
            grayBlue);
        Cv2.InRange(
            hsv,
            new Scalar(0, 14, bounds.ValueLow),
            new Scalar(34, 112, bounds.ValueHigh),
            lowBrown);
        Cv2.InRange(
            hsv,
            new Scalar(174, 14, bounds.ValueLow),
            new Scalar(179, 112, bounds.ValueHigh),
            highBrown);
        Cv2.InRange(
            hsv,
            new Scalar(0, 0, Math.Max(76, bounds.ValueLow)),
            new Scalar(179, 29, Math.Min(176, bounds.ValueHigh)),
            neutral);
        Cv2.BitwiseOr(grayBlue, lowBrown, seed);
        Cv2.BitwiseOr(seed, highBrown, seed);
        Cv2.BitwiseOr(seed, neutral, seed);
        using var labSupport = CreateLabSupport(labChannels, bounds);
        Cv2.BitwiseAnd(seed, labSupport, seed);
        using var brightWhite = new Mat();
        using var highChroma = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 184), new Scalar(179, 18, 255), brightWhite);
        Cv2.InRange(hsv, new Scalar(0, 145, 62), new Scalar(179, 255, 255), highChroma);
        Cv2.BitwiseNot(brightWhite, brightWhite);
        Cv2.BitwiseAnd(seed, brightWhite, seed);
        Cv2.BitwiseNot(highChroma, highChroma);
        Cv2.BitwiseAnd(seed, highChroma, seed);
        return seed;
    }

    private static Mat CreateLabSupport(IReadOnlyList<Mat> labChannels, AdaptiveBounds bounds)
    {
        using var lightnessRange = new Mat();
        Cv2.InRange(
            labChannels[0],
            new Scalar(bounds.LightnessLow),
            new Scalar(bounds.LightnessHigh),
            lightnessRange);
        using var a = new Mat();
        using var b = new Mat();
        labChannels[1].ConvertTo(a, MatType.CV_32F);
        labChannels[2].ConvertTo(b, MatType.CV_32F);
        Cv2.Subtract(a, new Scalar(128d), a);
        Cv2.Subtract(b, new Scalar(128d), b);
        Cv2.Multiply(a, a, a);
        Cv2.Multiply(b, b, b);
        using var chromaSquared = new Mat();
        Cv2.Add(a, b, chromaSquared);
        using var chromaSupport = new Mat();
        Cv2.Threshold(chromaSquared, chromaSupport, 52d * 52d, 255d, ThresholdTypes.BinaryInv);
        chromaSupport.ConvertTo(chromaSupport, MatType.CV_8U);
        var support = new Mat();
        Cv2.BitwiseAnd(lightnessRange, chromaSupport, support);
        return support;
    }

    private void RestrictToMapCanvas(Mat seed)
    {
        using var canvas = new Mat(seed.Size(), MatType.CV_8UC1, Scalar.Black);
        var left = Math.Clamp((int)Math.Round(seed.Width * _tuning.MapCanvasLeft), 0, seed.Width);
        var top = Math.Clamp((int)Math.Round(seed.Height * _tuning.MapCanvasTop), 0, seed.Height);
        var right = Math.Clamp((int)Math.Round(seed.Width * _tuning.MapCanvasRight), left, seed.Width);
        var bottom = Math.Clamp((int)Math.Round(seed.Height * _tuning.MapCanvasBottom), top, seed.Height);
        if (right > left && bottom > top)
            Cv2.Rectangle(canvas, new Rect(left, top, right - left, bottom - top), Scalar.White, -1);
        Cv2.BitwiseAnd(seed, canvas, seed);
    }

    private static Mat FilterComponents(Mat mask, int minimumArea, double minimumRadius)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            mask, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);
        var kept = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.Black);
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < minimumArea)
                continue;
            var rect = new Rect(
                stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Height));
            using var labelRegion = new Mat(labels, rect);
            using var component = new Mat();
            using var distance = new Mat();
            Cv2.Compare(labelRegion, label, component, CmpTypes.EQ);
            Cv2.DistanceTransform(component, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            Cv2.MinMaxLoc(distance, out _, out var maximumRadius, out _, out _);
            if (maximumRadius < minimumRadius)
                continue;
            using var destination = new Mat(kept, rect);
            Cv2.BitwiseOr(destination, component, destination);
        }
        return kept;
    }

    private static Mat FillSmallHoles(Mat mask, int maximumArea)
    {
        using var inverse = new Mat();
        Cv2.BitwiseNot(mask, inverse);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            inverse, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);
        var borderLabels = CollectBorderLabels(labels);
        var output = mask.Clone();
        using var component = new Mat();
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (borderLabels.Contains(label) || area > maximumArea)
                continue;
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(output, component, output);
        }
        return output;
    }

    private static HashSet<int> CollectBorderLabels(Mat labels)
    {
        var result = new HashSet<int>();
        var width = labels.Width;
        var height = labels.Height;
        for (var x = 0; x < width; x++)
        {
            result.Add(labels.At<int>(0, x));
            result.Add(labels.At<int>(height - 1, x));
        }
        for (var y = 0; y < height; y++)
        {
            result.Add(labels.At<int>(y, 0));
            result.Add(labels.At<int>(y, width - 1));
        }
        return result;
    }

    private static Mat CreateKernel(int radius) => Cv2.GetStructuringElement(
        MorphShapes.Ellipse,
        new Size(Math.Max(3, (radius * 2) + 1), Math.Max(3, (radius * 2) + 1)));

    private static int[] BuildHistogram(Mat channel, Mat mask)
    {
        var length = checked(channel.Rows * channel.Cols);
        var values = new byte[length];
        var selected = new byte[length];
        Marshal.Copy(channel.Data, values, 0, length);
        Marshal.Copy(mask.Data, selected, 0, length);
        var histogram = new int[256];
        for (var index = 0; index < length; index++)
        {
            if (selected[index] != 0)
                histogram[values[index]]++;
        }
        return histogram;
    }

    private static int Percentile(IReadOnlyList<int> histogram, int count, double percentile)
    {
        var target = Math.Clamp((int)Math.Ceiling(count * percentile), 1, count);
        var cumulative = 0;
        for (var value = 0; value < histogram.Count; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
                return value;
        }
        return histogram.Count - 1;
    }

    private static void DisposeAll(IEnumerable<Mat> mats)
    {
        foreach (var mat in mats)
            mat.Dispose();
    }

    private readonly record struct AdaptiveBounds(
        int ValueLow,
        int ValueHigh,
        int LightnessLow,
        int LightnessHigh);
}
