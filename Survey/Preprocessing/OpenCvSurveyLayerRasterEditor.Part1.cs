using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Preprocessing.OpenCv;
public sealed partial class OpenCvSurveyLayerRasterEditor : ISurveyLayerRasterEditor
{

    public async Task<ReadOnlyMemory<byte>> RenderLayerAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        CancellationToken cancellationToken = default)
    {
        var selected = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
        using var source = await ReadImageAsync(projectId, selected, ImreadModes.Unchanged, cancellationToken)
            .ConfigureAwait(false);
        using var bgra = new Mat();
        if (source.Channels() == 4)
            source.CopyTo(bgra);
        else if (source.Channels() == 3)
            Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        else if (source.Channels() == 1)
            Cv2.CvtColor(source, bgra, ColorConversionCodes.GRAY2BGRA);
        else if (source.Channels() == 2)
        {
            var channels = Cv2.Split(source);
            try
            {
                Cv2.Merge([channels[0], channels[0], channels[0], channels[1]], bgra);
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported survey layer channel count: {source.Channels()}.");
        }
        ApplyBrightness(bgra, layer.Brightness);
        if (layer.HiddenMaskAsset is not null)
        {
            using var hidden = await ReadMaskAsync(
                projectId,
                layer.HiddenMaskAsset,
                bgra.Width,
                bgra.Height,
                cancellationToken).ConfigureAwait(false);
            using var visible = new Mat();
            Cv2.BitwiseNot(hidden, visible);
            var channels = Cv2.Split(bgra);
            try
            {
                Cv2.BitwiseAnd(channels[3], visible, channels[3]);
                Cv2.Merge(channels, bgra);
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }
        }
        Cv2.ImEncode(".png", bgra, out var bytes);
        return bytes;
    }

    private static void ApplyBrightness(Mat bgra, double brightness)
    {
        if (Math.Abs(brightness - 1d) < 0.000001d)
            return;
        var channels = Cv2.Split(bgra);
        try
        {
            for (var index = 0; index < 3; index++)
                channels[index].ConvertTo(channels[index], MatType.CV_8UC1, brightness);
            Cv2.Merge(channels, bgra);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private async Task<Mat> ReadMaskAsync(
        Guid projectId,
        SurveyAssetReference? asset,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (asset is null)
            return new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.Black);
        var mask = await ReadImageAsync(projectId, asset, ImreadModes.Grayscale, cancellationToken)
            .ConfigureAwait(false);
        if (mask.Width == width && mask.Height == height)
            return mask;
        using (mask)
        {
            var resized = new Mat();
            Cv2.Resize(mask, resized, new Size(width, height), interpolation: InterpolationFlags.Nearest);
            return resized;
        }
    }

    private async Task<Mat> ReadImageAsync(
        Guid projectId,
        SurveyAssetReference asset,
        ImreadModes mode,
        CancellationToken cancellationToken)
    {
        await using var stream = await _assets.OpenReadAsync(projectId, asset, cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var image = Cv2.ImDecode(memory.ToArray(), mode);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidDataException($"测绘图层资产无法解码：{asset.Sha256}");
        }
        return image;
    }

    private static IEnumerable<SurveyWorldPoint> Interpolate(
        IReadOnlyList<SurveyWorldPoint> points,
        double spacing)
    {
        if (points.Count == 0)
            yield break;
        yield return points[0];
        for (var index = 1; index < points.Count; index++)
        {
            var from = points[index - 1];
            var to = points[index];
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var count = Math.Max(1, (int)Math.Ceiling(distance / spacing));
            for (var step = 1; step <= count; step++)
            {
                var amount = step / (double)count;
                yield return new SurveyWorldPoint(from.X + (dx * amount), from.Y + (dy * amount));
            }
        }
    }

    private static IReadOnlyList<SurveyWorldPoint> CreatePolygon(
        SurveyWorldPoint center,
        double size,
        SurveyBrushShape shape)
    {
        var radius = size / 2d;
        if (shape == SurveyBrushShape.Square)
        {
            return
            [
                new(center.X - radius, center.Y - radius),
                new(center.X + radius, center.Y - radius),
                new(center.X + radius, center.Y + radius),
                new(center.X - radius, center.Y + radius)
            ];
        }
        return Enumerable.Range(0, 32)
            .Select(index => index * Math.PI * 2d / 32d)
            .Select(angle => new SurveyWorldPoint(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius)))
            .ToArray();
    }
}
