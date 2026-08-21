using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Parameters for the shape-only structure extraction method.
/// The defaults target the dark-background map viewport captures used by IDVB.
/// </summary>
public sealed record StructureFillOptions
{
    /// <summary>Apply the guide-map tone profile before extracting shape.</summary>
    public bool ApplyGuideMapTone { get; init; }

    public int ThresholdOffset { get; init; } = 8;
    // Keep this gate below the raw Otsu split so that dark room floors remain
    // part of a structure while a near-black capture background is rejected.
    public int RawIntensityThresholdDrop { get; init; } = 12;
    public int MinimumThreshold { get; init; } = 70;
    public int MaximumThreshold { get; init; } = 180;
    public int MinimumComponentArea { get; init; } = 750;
    public double MinimumComponentAreaRatio { get; init; } = 0.00015d;

    /// <summary>Only the thin capture frame is cleared.</summary>
    public double BorderClearRatio { get; init; } = 0.002d;

    internal void Validate()
    {
        if (ThresholdOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(ThresholdOffset));
        if (RawIntensityThresholdDrop < 0)
            throw new ArgumentOutOfRangeException(nameof(RawIntensityThresholdDrop));
        if (MinimumThreshold < 0 || MinimumThreshold > 255)
            throw new ArgumentOutOfRangeException(nameof(MinimumThreshold));
        if (MaximumThreshold < MinimumThreshold || MaximumThreshold > 255)
            throw new ArgumentOutOfRangeException(nameof(MaximumThreshold));
        if (MinimumComponentArea < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumComponentArea));
        if (!double.IsFinite(MinimumComponentAreaRatio)
            || MinimumComponentAreaRatio < 0d
            || MinimumComponentAreaRatio > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumComponentAreaRatio));
        }
        if (!double.IsFinite(BorderClearRatio)
            || BorderClearRatio < 0d
            || BorderClearRatio >= 0.5d)
        {
            throw new ArgumentOutOfRangeException(nameof(BorderClearRatio));
        }
    }
}

/// <summary>
/// Output of one structure-fill pass. The mask is an owned CV_8UC1 image:
/// white is classified structural area and black is classified background.
/// </summary>
public sealed class StructureFillResult : IDisposable
{
    internal StructureFillResult(
        Mat mask,
        double otsuThreshold,
        double effectiveThreshold,
        int componentCount,
        Rect bounds)
    {
        Mask = mask;
        OtsuThreshold = otsuThreshold;
        EffectiveThreshold = effectiveThreshold;
        ComponentCount = componentCount;
        Bounds = bounds;
        ForegroundPixels = Cv2.CountNonZero(mask);
    }

    public Mat Mask { get; }
    public double OtsuThreshold { get; }
    public double EffectiveThreshold { get; }
    public int ComponentCount { get; }
    public int ForegroundPixels { get; }
    public Rect Bounds { get; }
    public bool HasStructure => ForegroundPixels > 0;

    public void Dispose() => Mask.Dispose();
}

/// <summary>
/// Shape-only replacement building block for the old feature-heavy
/// StructureRegistration preprocessor. It accepts one image and returns a
/// shape mask of every sufficiently large visible structure component. Pixels
/// are retained by image classification; enclosed background is never filled
/// merely because a contour surrounds it. No descriptors, feature voting,
/// reference image, or alignment state is used.
/// </summary>
public sealed class MapStructureFiller
{
    public const int AlgorithmVersion = 2;

    public Mat Fill(Mat source, StructureFillOptions? options = null)
    {
        using var result = Analyze(source, options);
        return result.Mask.Clone();
    }

    public StructureFillResult Analyze(
        Mat source,
        StructureFillOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("结构填充不能处理空图像。", nameof(source));

        options ??= new StructureFillOptions();
        options.Validate();

        using var bgr = ToBgr(source);
        using var preparedBgr = options.ApplyGuideMapTone
            ? ApplyGuideMapTone(bgr)
            : bgr.Clone();
        using var gray = new Mat();
        using var hsv = new Mat();
        using var normalizedGray = new Mat();
        using var blurred = new Mat();
        Cv2.CvtColor(preparedBgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(preparedBgr, hsv, ColorConversionCodes.BGR2HSV);
        using (var clahe = Cv2.CreateCLAHE(2d, new Size(8, 8)))
            clahe.Apply(gray, normalizedGray);
        Cv2.GaussianBlur(normalizedGray, blurred, new Size(5, 5), 0d);

        using var otsuMask = new Mat();
        var otsuThreshold = Cv2.Threshold(
            blurred,
            otsuMask,
            0d,
            255d,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var effectiveThreshold = Math.Clamp(
            otsuThreshold + options.ThresholdOffset,
            options.MinimumThreshold,
            options.MaximumThreshold);

        // CLAHE is useful for map walls, but it can lift a perfectly uniform
        // dark background to mid-gray. Gate the normalized result with a raw
        // luminance mask so a dark screenshot background never becomes one
        // giant foreground component.
        using var rawOtsuMask = new Mat();
        var rawOtsuThreshold = Cv2.Threshold(
            gray,
            rawOtsuMask,
            0d,
            255d,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var rawIntensityThreshold = Math.Clamp(
            rawOtsuThreshold - options.RawIntensityThresholdDrop,
            45d,
            220d);
        using var rawIntensityMask = new Mat();
        Cv2.Threshold(
            gray,
            rawIntensityMask,
            rawIntensityThreshold,
            255d,
            ThresholdTypes.Binary);

        using var structure = new Mat();
        Cv2.Threshold(
            blurred,
            structure,
            effectiveThreshold,
            255d,
            ThresholdTypes.Binary);
        Cv2.BitwiseAnd(structure, rawIntensityMask, structure);

        using var nuisance = BuildNuisanceMask(hsv);
        using var inverseNuisance = new Mat();
        Cv2.BitwiseNot(nuisance, inverseNuisance);
        Cv2.BitwiseAnd(structure, inverseNuisance, structure);
        if (options.ApplyGuideMapTone)
        {
            // Guide-map route lines are often saturated. They must not be
            // discarded together with compact colored markers.
            using var guideLines = BuildGuideMapLineMask(nuisance);
            Cv2.BitwiseAnd(guideLines, rawIntensityMask, guideLines);
            Cv2.BitwiseOr(structure, guideLines, structure);
        }

        using var closeKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(5, 5));
        using var openKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(3, 3));
        Cv2.MorphologyEx(structure, structure, MorphTypes.Close, closeKernel);
        Cv2.MorphologyEx(structure, structure, MorphTypes.Open, openKernel);

        // Morphology may bridge a narrow gap. Re-apply the image-derived
        // non-background gate so it cannot manufacture white background areas.
        Cv2.BitwiseAnd(structure, rawIntensityMask, structure);

        ClearCaptureBorder(structure, options.BorderClearRatio);

        var minimumArea = Math.Max(
            options.MinimumComponentArea,
            (int)Math.Round(structure.Width * structure.Height
                * options.MinimumComponentAreaRatio));
        var componentCount = KeepLargeComponents(structure, minimumArea);

        using var nonZero = new Mat();
        Cv2.FindNonZero(structure, nonZero);
        var bounds = nonZero.Empty()
            ? new Rect()
            : Cv2.BoundingRect(nonZero);

        return new StructureFillResult(
            structure.Clone(),
            otsuThreshold,
            effectiveThreshold,
            componentCount,
            bounds);
    }

    private static Mat BuildNuisanceMask(Mat hsv)
    {
        var channels = Cv2.Split(hsv);
        try
        {
            using var saturated = new Mat();
            using var bright = new Mat();
            var nuisance = new Mat();
            Cv2.Threshold(channels[1], saturated, 105d, 255d, ThresholdTypes.Binary);
            Cv2.Threshold(channels[2], bright, 70d, 255d, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(saturated, bright, nuisance);
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(3, 3));
            Cv2.Dilate(nuisance, nuisance, kernel, iterations: 1);
            return nuisance;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static Mat BuildGuideMapLineMask(Mat nuisance)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            nuisance,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8);
        var lines = Mat.Zeros(nuisance.Size(), MatType.CV_8UC1).ToMat();
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(
                label,
                (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(
                label,
                (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(
                label,
                (int)ConnectedComponentsTypes.Height);
            if (area < 20 || width < 2 || height < 2)
                continue;

            var aspect = Math.Max(width, height) / (double)Math.Min(width, height);
            var fillRatio = area / (double)(width * height);
            if (aspect < 3d && (fillRatio > 0.45d || width + height < 24))
                continue;

            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(lines, component, lines);
        }

        return lines;
    }

    private static Mat ApplyGuideMapTone(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        try
        {
            using var lut = BuildGuideMapToneLookup();
            using var adjustedValue = new Mat();
            Cv2.LUT(channels[2], lut, adjustedValue);
            adjustedValue.CopyTo(channels[2]);

            using var adjustedHsv = new Mat();
            Cv2.Merge(channels, adjustedHsv);
            var adjustedBgr = new Mat();
            Cv2.CvtColor(adjustedHsv, adjustedBgr, ColorConversionCodes.HSV2BGR);
            return adjustedBgr;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static Mat BuildGuideMapToneLookup()
    {
        using var lut = new Mat(1, 256, MatType.CV_8UC1);
        for (var index = 0; index < 256; index++)
        {
            var value = index / 255d;

            // Slight brightness lift, medium exposure lift, and large contrast lift.
            value = Math.Clamp(value + 0.03d, 0d, 1d);
            value = Math.Clamp(value * Math.Pow(2d, 0.35d), 0d, 1d);
            value = Math.Clamp(0.5d + ((value - 0.5d) * 1.65d), 0d, 1d);

            // Large highlight lift and very strong shadow reduction.
            value = Math.Clamp(
                value + (0.32d * SmoothStep(0.52d, 1d, value)),
                0d,
                1d);
            value = Math.Clamp(
                value - (0.72d * (1d - SmoothStep(0d, 0.55d, value))),
                0d,
                1d);

            lut.Set(0, index, (byte)Math.Round(value * 255d));
        }

        return lut.Clone();
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0d, 1d);
        return t * t * (3d - (2d * t));
    }

    private static int KeepLargeComponents(Mat binary, int minimumArea)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            binary,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8);
        if (count <= 1)
            return 0;

        using var kept = Mat.Zeros(binary.Size(), MatType.CV_8UC1).ToMat();
        var keptCount = 0;
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(
                label,
                (int)ConnectedComponentsTypes.Area);
            if (area < minimumArea)
                continue;

            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(kept, component, kept);
            keptCount++;
        }

        kept.CopyTo(binary);
        return keptCount;
    }

    private static void ClearCaptureBorder(Mat binary, double ratio)
    {
        var border = Math.Max(
            1,
            (int)Math.Round(Math.Min(binary.Width, binary.Height) * ratio));
        Cv2.Rectangle(
            binary,
            new Rect(0, 0, binary.Width, binary.Height),
            Scalar.Black,
            border);
    }

    private static Mat ToBgr(Mat source)
    {
        var bgr = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                source.CopyTo(bgr);
                break;
            case 1:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
            default:
                bgr.Dispose();
                throw new ArgumentException(
                    "结构填充只支持 1/3/4 通道图像。",
                    nameof(source));
        }
        return bgr;
    }
}
