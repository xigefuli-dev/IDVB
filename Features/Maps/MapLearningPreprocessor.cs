using OpenCvSharp;
using TorchSharp;
using static TorchSharp.torch;
using CvSize = OpenCvSharp.Size;

namespace IDVBuff.Features.Maps;

internal sealed record MapLearningReferenceTile(
    float[] Input,
    double CenterX,
    double CenterY,
    double Extent);

internal static class MapLearningPreprocessor
{
    public const string Version =
        "spatial-focus-gray-edge-128-v5-full-floor-tiles";
    public const int ObservationSize = 500;
    public const int InputSize = 128;
    public const int ChannelCount = 2;
    private static readonly double[] TrainingPaddingScales =
        [1.05d, 1.15d, 1.25d, 1.35d, 1.45d, 1.60d, 1.80d, 2.00d, 2.25d];

    public static float[] CreateInput(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("CNN 输入图像为空。", nameof(source));

        using var observation = CreateNetworkObservation(source, 1.35d);
        return CreateInputFromObservation(observation);
    }

    private static float[] CreateInputFromObservation(Mat observation)
    {
        using var gray = ToGray(observation);
        using var resizedGray = new Mat();
        Cv2.Resize(gray, resizedGray, new CvSize(InputSize, InputSize),
            interpolation: InterpolationFlags.Area);
        using var fullEdges = new Mat();
        Cv2.Canny(gray, fullEdges, 50d, 150d, 3, true);
        using var resizedEdges = new Mat();
        Cv2.Resize(fullEdges, resizedEdges, new CvSize(InputSize, InputSize),
            interpolation: InterpolationFlags.Area);

        var plane = InputSize * InputSize;
        var result = new float[ChannelCount * plane];
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var index = y * InputSize + x;
                result[index] = resizedGray.At<byte>(y, x) / 255f;
                result[plane + index] = resizedEdges.At<byte>(y, x) / 255f;
            }
        }
        return result;
    }

    public static IReadOnlyList<float[]> CreateTrainingInputs(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("CNN 训练图像为空。", nameof(source));

        var results = new List<float[]>(TrainingPaddingScales.Length);
        foreach (var paddingScale in TrainingPaddingScales)
        {
            using var observation = CreateNetworkObservation(
                source, paddingScale);
            results.Add(CreateInputFromObservation(observation));
        }
        return results;
    }

    public static Tensor CreateGpuTrainingTensor(
        Mat source,
        Device device)
    {
        if (device.type != DeviceType.CUDA)
            throw new ArgumentException("GPU 训练输入需要 CUDA 设备。",
                nameof(device));
        return SiameseMapNetwork.ToTensor(
            CreateTrainingInputs(source), device);
    }

    public static IReadOnlyList<MapLearningReferenceTile> CreateReferenceTiles(
        Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("攻略地图楼层图像为空。", nameof(source));

        var tiles = new List<MapLearningReferenceTile>(14);
        AddGridTiles(source, tiles, 0.36d, [0.20d, 0.50d, 0.80d]);
        AddGridTiles(source, tiles, 0.62d, [0.31d, 0.69d]);
        tiles.Add(new MapLearningReferenceTile(
            CreateInput(source), 0.5d, 0.5d, 1d));
        return tiles;
    }

    private static void AddGridTiles(
        Mat source,
        ICollection<MapLearningReferenceTile> target,
        double extent,
        IReadOnlyList<double> centers)
    {
        var side = Math.Max(16, (int)Math.Round(
            Math.Min(source.Width, source.Height) * extent));
        foreach (var centerY in centers)
        foreach (var centerX in centers)
        {
            var left = Math.Clamp(
                (int)Math.Round(centerX * source.Width - side / 2d),
                0, Math.Max(0, source.Width - side));
            var top = Math.Clamp(
                (int)Math.Round(centerY * source.Height - side / 2d),
                0, Math.Max(0, source.Height - side));
            using var region = new Mat(source, new Rect(left, top, side, side));
            target.Add(new MapLearningReferenceTile(
                CreateInput(region),
                (left + side / 2d) / source.Width,
                (top + side / 2d) / source.Height,
                extent));
        }
    }

    public static Mat CreateCanonicalObservation(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("CNN 观察区域为空。", nameof(source));

        var side = Math.Min(source.Width, source.Height);
        var left = (source.Width - side) / 2;
        var top = (source.Height - side) / 2;
        using var square = new Mat(source, new Rect(left, top, side, side));
        if (side == ObservationSize)
            return square.Clone();
        var observation = new Mat();
        Cv2.Resize(square, observation,
            new CvSize(ObservationSize, ObservationSize), interpolation:
                side > ObservationSize
                    ? InterpolationFlags.Area
                    : InterpolationFlags.Linear);
        return observation;
    }

    internal static Mat CreateNetworkObservation(
        Mat source,
        double paddingScale)
    {
        using var canonical = CreateCanonicalObservation(source);
        using var gray = ToGray(canonical);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new CvSize(5, 5), 0d);
        using var foreground = new Mat();
        Cv2.Threshold(blurred, foreground, 0d, 255d,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var points = new Mat();
        Cv2.FindNonZero(foreground, points);
        if (points.Empty())
            return canonical.Clone();
        var bounds = Cv2.BoundingRect(points);
        if (bounds.Width < 12 || bounds.Height < 12)
            return canonical.Clone();

        var side = Math.Clamp((int)Math.Ceiling(
            Math.Max(bounds.Width, bounds.Height)
                * Math.Clamp(paddingScale, 1d, 3d)),
            16, Math.Min(canonical.Width, canonical.Height));
        var centerX = bounds.X + bounds.Width / 2d;
        var centerY = bounds.Y + bounds.Height / 2d;
        var left = Math.Clamp((int)Math.Round(centerX - side / 2d),
            0, canonical.Width - side);
        var top = Math.Clamp((int)Math.Round(centerY - side / 2d),
            0, canonical.Height - side);
        using var focused = new Mat(canonical,
            new Rect(left, top, side, side));
        if (side == ObservationSize)
            return focused.Clone();
        var result = new Mat();
        Cv2.Resize(focused, result,
            new CvSize(ObservationSize, ObservationSize), interpolation:
                side > ObservationSize
                    ? InterpolationFlags.Area
                    : InterpolationFlags.Linear);
        return result;
    }

    public static Mat LoadReferenceRegion(MapRecognitionChoice choice)
    {
        var path = choice.Recognition.FloorImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("候选地图缺少扫描楼层参考图。", path);

        using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidDataException("候选地图参考图无法解码。");

        return source.Clone();
    }

    public static byte[] EncodePrivacyScopedPng(Mat viewport)
    {
        if (viewport.Empty())
            throw new ArgumentException("训练视口为空。", nameof(viewport));
        using var observation = CreateCanonicalObservation(viewport);
        return observation.ImEncode(".png", new ImageEncodingParam(
            ImwriteFlags.PngCompression, 6));
    }

    public static byte[] EncodeReferenceFloorPng(Mat reference)
    {
        if (reference.Empty())
            throw new ArgumentException("攻略地图楼层图像为空。", nameof(reference));
        var scale = Math.Min(
            (double)ObservationSize / reference.Width,
            (double)ObservationSize / reference.Height);
        var width = Math.Max(1, (int)Math.Round(reference.Width * scale));
        var height = Math.Max(1, (int)Math.Round(reference.Height * scale));
        using var resized = new Mat();
        Cv2.Resize(reference, resized, new CvSize(width, height), interpolation:
            scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
        using var canvas = new Mat(ObservationSize, ObservationSize,
            reference.Type(), OpenCvSharp.Scalar.All(0));
        var left = (ObservationSize - width) / 2;
        var top = (ObservationSize - height) / 2;
        using (var destination = new Mat(canvas, new Rect(left, top, width, height)))
            resized.CopyTo(destination);
        return canvas.ImEncode(".png", new ImageEncodingParam(
            ImwriteFlags.PngCompression, 6));
    }

    private static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1)
            return source.Clone();
        var gray = new Mat();
        Cv2.CvtColor(
            source,
            gray,
            source.Channels() == 4
                ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

}
