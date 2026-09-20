using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Preprocessing.OpenCv;

public sealed partial class OpenCvSurveyLayerRasterEditor : ISurveyLayerRasterEditor
{
    private const double MaximumVignetteGain = 2d;
    private readonly ISurveyAssetStore _assets;

    public OpenCvSurveyLayerRasterEditor(ISurveyAssetStore assets) => _assets = assets;

    public async Task<SurveyAssetReference> CorrectVignetteAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        double compensationStart,
        double compensationStrength,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(compensationStart) || compensationStart is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(compensationStart));
        if (!double.IsFinite(compensationStrength) || compensationStrength is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(compensationStrength));

        var selected = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
        using var source = await ReadImageAsync(projectId, selected, ImreadModes.Unchanged, cancellationToken)
            .ConfigureAwait(false);
        using var bgr = ToBgr(source);
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var labChannels = Cv2.Split(lab);
        Mat? alpha = null;
        try
        {
            if (source.Channels() is 2 or 4)
            {
                alpha = new Mat();
                Cv2.ExtractChannel(source, alpha, source.Channels() - 1);
            }
            ApplyVignetteToLightness(
                labChannels[0],
                alpha,
                compensationStart,
                compensationStrength,
                cancellationToken);
            using var adjustedLab = new Mat();
            using var adjustedBgr = new Mat();
            Cv2.Merge(labChannels, adjustedLab);
            Cv2.CvtColor(adjustedLab, adjustedBgr, ColorConversionCodes.Lab2BGR);
            using var output = new Mat();
            if (alpha is not null)
            {
                Cv2.CvtColor(adjustedBgr, output, ColorConversionCodes.BGR2BGRA);
                Cv2.InsertChannel(alpha, output, 3);
            }
            else
            {
                adjustedBgr.CopyTo(output);
            }

            Cv2.ImEncode(".png", output, out var bytes);
            return await _assets.PutAsync(
                projectId,
                new SurveyEncodedFrame(
                    bytes,
                    ".png",
                    "image/png",
                    output.Width,
                    output.Height,
                    observation.Capture),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            alpha?.Dispose();
            foreach (var channel in labChannels)
                channel.Dispose();
        }
    }

    private static void ApplyVignetteToLightness(
        Mat lightness,
        Mat? alpha,
        double compensationStart,
        double compensationStrength,
        CancellationToken cancellationToken)
    {
        if (compensationStrength <= double.Epsilon)
            return;
        var width = lightness.Width;
        var height = lightness.Height;
        var centerX = (width - 1d) / 2d;
        var centerY = (height - 1d) / 2d;
        var radiusX = Math.Max(0.5d, Math.Max(centerX, width - 1d - centerX));
        var radiusY = Math.Max(0.5d, Math.Max(centerY, height - 1d - centerY));
        // At 100%, retain a one-thousandth edge band so the mathematical
        // endpoint remains useful without a division-by-zero discontinuity.
        var start = Math.Min(0.999d, compensationStart);
        var maximumGain = Math.Min(MaximumVignetteGain, 1d + compensationStrength);

        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var normalizedY = (y - centerY) / radiusY;
            for (var x = 0; x < width; x++)
            {
                if (alpha is not null && alpha.Get<byte>(y, x) == 0)
                    continue;
                var normalizedX = (x - centerX) / radiusX;
                var distance = Math.Min(1d, Math.Sqrt(
                    ((normalizedX * normalizedX) + (normalizedY * normalizedY)) / 2d));
                if (distance <= start)
                    continue;
                var amount = Math.Clamp((distance - start) / (1d - start), 0d, 1d);
                var weight = amount * amount * (3d - (2d * amount));
                var gain = Math.Min(maximumGain, 1d + (compensationStrength * weight));
                var value = lightness.Get<byte>(y, x);
                var normalizedLightness = value / 255d;
                // Roll the gain off smoothly in existing highlights. This keeps
                // the correction in Lab lightness while protecting near-white detail.
                var highlight = Math.Clamp((normalizedLightness - 0.72d) / 0.28d, 0d, 1d);
                var highlightProtection = highlight * highlight * (3d - (2d * highlight));
                var protectedGain = 1d + ((gain - 1d) * (1d - highlightProtection));
                lightness.Set(y, x, (byte)Math.Clamp(
                    Math.Round(value * protectedGain), 0d, 255d));
            }
        }
    }

    public async Task<SurveyAssetReference> NormalizeColorsAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        SurveyMapLayer anchorLayer,
        SurveyObservation anchorObservation,
        CancellationToken cancellationToken = default)
    {
        var sourceAsset = layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset : observation.SourceAsset;
        var anchorAsset = anchorLayer.ColorFilterAsset ?? (anchorLayer.UsesCleanedDisplay && anchorObservation.DisplayAsset is not null
            ? anchorObservation.DisplayAsset : anchorObservation.SourceAsset);
        using var source = await ReadImageAsync(projectId, sourceAsset, ImreadModes.Unchanged, cancellationToken)
            .ConfigureAwait(false);
        using var reference = await ReadImageAsync(projectId, anchorAsset, ImreadModes.Unchanged, cancellationToken)
            .ConfigureAwait(false);
        using var sourceBgr = ToBgr(source);
        using var referenceBgr = ToBgr(reference);
        using var sourceLab = new Mat();
        using var alignedReference = AlignReferenceToSource(
            referenceBgr, sourceBgr.Size(), layer.EffectiveTransform, anchorLayer.EffectiveTransform);
        using var alignedReferenceLab = new Mat();
        Cv2.CvtColor(sourceBgr, sourceLab, ColorConversionCodes.BGR2Lab);
        Cv2.CvtColor(alignedReference, alignedReferenceLab, ColorConversionCodes.BGR2Lab);
        using var sourceMask = CreateContentMask(source);
        using var referenceMask = CreateContentMask(reference);
        using var alignedReferenceMask = AlignReferenceToSource(
            referenceMask, sourceBgr.Size(), layer.EffectiveTransform, anchorLayer.EffectiveTransform,
            InterpolationFlags.Nearest);
        using var overlapMask = new Mat();
        Cv2.BitwiseAnd(sourceMask, alignedReferenceMask, overlapMask);
        var minimumOverlap = Math.Max(64, (source.Width * source.Height) / 1000);
        Scalar sourceMean;
        Scalar sourceStd;
        Scalar referenceMean;
        Scalar referenceStd;
        if (Cv2.CountNonZero(overlapMask) >= minimumOverlap)
        {
            Cv2.MeanStdDev(sourceLab, out sourceMean, out sourceStd, overlapMask);
            Cv2.MeanStdDev(alignedReferenceLab, out referenceMean, out referenceStd, overlapMask);
        }
        else
        {
            using var referenceLab = new Mat();
            Cv2.CvtColor(referenceBgr, referenceLab, ColorConversionCodes.BGR2Lab);
            Cv2.MeanStdDev(sourceLab, out sourceMean, out sourceStd, sourceMask);
            Cv2.MeanStdDev(referenceLab, out referenceMean, out referenceStd, referenceMask);
        }

        var channels = Cv2.Split(sourceLab);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var scale = referenceStd[index] / Math.Max(1d, sourceStd[index]);
                scale = Math.Clamp(scale, 0.5d, 2d);
                channels[index].ConvertTo(
                    channels[index], MatType.CV_8UC1, scale,
                    referenceMean[index] - (sourceMean[index] * scale));
            }
            using var adjustedLab = new Mat();
            using var adjustedBgr = new Mat();
            Cv2.Merge(channels, adjustedLab);
            Cv2.CvtColor(adjustedLab, adjustedBgr, ColorConversionCodes.Lab2BGR);
            using var output = new Mat();
            if (source.Channels() == 4)
            {
                var originalChannels = Cv2.Split(source);
                try { Cv2.CvtColor(adjustedBgr, output, ColorConversionCodes.BGR2BGRA); Cv2.InsertChannel(originalChannels[3], output, 3); }
                finally { foreach (var channel in originalChannels) channel.Dispose(); }
            }
            else
                adjustedBgr.CopyTo(output);
            Cv2.ImEncode(".png", output, out var bytes);
            return await _assets.PutAsync(projectId, new SurveyEncodedFrame(
                bytes, ".png", "image/png", output.Width, output.Height, observation.Capture), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var channel in channels) channel.Dispose();
        }
    }

    private static Mat ToBgr(Mat image)
    {
        var result = new Mat();
        if (image.Channels() == 4) Cv2.CvtColor(image, result, ColorConversionCodes.BGRA2BGR);
        else if (image.Channels() == 3) image.CopyTo(result);
        else if (image.Channels() == 1) Cv2.CvtColor(image, result, ColorConversionCodes.GRAY2BGR);
        else if (image.Channels() == 2)
        {
            using var gray = new Mat();
            Cv2.ExtractChannel(image, gray, 0);
            Cv2.CvtColor(gray, result, ColorConversionCodes.GRAY2BGR);
        }
        else throw new InvalidDataException($"Unsupported survey color-filter channel count: {image.Channels()}.");
        return result;
    }

    private static Mat CreateContentMask(Mat image)
    {
        var mask = new Mat();
        using var bgr = ToBgr(image);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, mask, 4d, 255d, ThresholdTypes.Binary);
        if (image.Channels() is 2 or 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(image, alpha, image.Channels() - 1);
            Cv2.BitwiseAnd(mask, alpha, mask);
        }
        return mask;
    }

    private static Mat AlignReferenceToSource(
        Mat reference,
        Size sourceSize,
        SurveyLayerTransform sourceTransform,
        SurveyLayerTransform referenceTransform,
        InterpolationFlags interpolation = InterpolationFlags.Linear)
    {
        var origin = referenceTransform.InverseTransform(sourceTransform.Transform(new SurveyWorldPoint(0d, 0d)));
        var xBasis = referenceTransform.InverseTransform(sourceTransform.Transform(new SurveyWorldPoint(1d, 0d)));
        var yBasis = referenceTransform.InverseTransform(sourceTransform.Transform(new SurveyWorldPoint(0d, 1d)));
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, xBasis.X - origin.X);
        matrix.Set(0, 1, yBasis.X - origin.X);
        matrix.Set(0, 2, origin.X);
        matrix.Set(1, 0, xBasis.Y - origin.Y);
        matrix.Set(1, 1, yBasis.Y - origin.Y);
        matrix.Set(1, 2, origin.Y);
        var aligned = new Mat();
        Cv2.WarpAffine(
            reference,
            aligned,
            matrix,
            sourceSize,
            interpolation | InterpolationFlags.WarpInverseMap,
            BorderTypes.Constant,
            Scalar.Black);
        return aligned;
    }

    public async Task<SurveyAssetReference?> ApplyHiddenMaskAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        IReadOnlyList<SurveyWorldPoint> worldPoints,
        double size,
        SurveyBrushShape shape,
        CancellationToken cancellationToken = default)
    {
        using var mask = await ReadMaskAsync(
            projectId,
            layer.HiddenMaskAsset,
            observation.SourceAsset.PixelWidth,
            observation.SourceAsset.PixelHeight,
            cancellationToken).ConfigureAwait(false);
        using var original = mask.Clone();
        var transform = layer.EffectiveTransform;
        var stamps = Interpolate(worldPoints, Math.Max(1d, size / 4d));
        foreach (var point in stamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var polygon = CreatePolygon(point, size, shape)
                .Select(item => transform.InverseTransform(item))
                .Select(item => new Point(Math.Round(item.X), Math.Round(item.Y)))
                .ToArray();
            var bounds = Cv2.BoundingRect(polygon);
            if (bounds.Right <= 0 || bounds.Bottom <= 0 || bounds.Left >= mask.Width || bounds.Top >= mask.Height)
                continue;
            Cv2.FillConvexPoly(mask, polygon, Scalar.White, LineTypes.Link8);
        }
        if (Cv2.Norm(mask, original, NormTypes.INF) <= 0d)
            return null;
        Cv2.ImEncode(".png", mask, out var bytes);
        return await _assets.PutAsync(
            projectId,
            new SurveyEncodedFrame(
                bytes,
                ".png",
                "image/png",
                mask.Width,
                mask.Height,
                observation.Capture),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyAssetReference?> ApplyColorBrushAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        IReadOnlyList<SurveyWorldPoint> worldPoints,
        double size,
        SurveyBrushShape shape,
        SurveyColor color,
        CancellationToken cancellationToken = default)
    {
        if (worldPoints.Count == 0 || !double.IsFinite(size) || size is < 1d or > 1024d)
            return null;
        var selected = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset : observation.SourceAsset);
        using var source = await ReadImageAsync(projectId, selected, ImreadModes.Unchanged, cancellationToken).ConfigureAwait(false);
        using var image = ToBgraForPainting(source);
        using var original = image.Clone();
        var transform = layer.EffectiveTransform;
        foreach (var point in Interpolate(worldPoints, Math.Max(1d, size / 4d)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixel = transform.InverseTransform(point);
            var center = new Point2f((float)pixel.X, (float)pixel.Y);
            var radius = (float)(size / 2d);
            var bounds = new Rect((int)Math.Floor(pixel.X - radius), (int)Math.Floor(pixel.Y - radius),
                (int)Math.Ceiling(size) + 1, (int)Math.Ceiling(size) + 1);
            var clipped = new Rect(Math.Max(0, bounds.X), Math.Max(0, bounds.Y),
                Math.Min(image.Width, bounds.Right) - Math.Max(0, bounds.X),
                Math.Min(image.Height, bounds.Bottom) - Math.Max(0, bounds.Y));
            if (clipped.Width <= 0 || clipped.Height <= 0) continue;
            using var stamp = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
            if (shape == SurveyBrushShape.Circle)
                Cv2.Circle(stamp, new Point((int)Math.Round(pixel.X), (int)Math.Round(pixel.Y)),
                    (int)Math.Ceiling(radius), Scalar.White, -1, LineTypes.Link8);
            else
                Cv2.Rectangle(stamp, new Rect((int)Math.Round(pixel.X - radius), (int)Math.Round(pixel.Y - radius),
                    Math.Max(1, (int)Math.Ceiling(size)), Math.Max(1, (int)Math.Ceiling(size))), Scalar.White, -1, LineTypes.Link8);
            var channels = Cv2.Split(image);
            try
            {
                channels[0].SetTo(new Scalar(color.B), stamp);
                channels[1].SetTo(new Scalar(color.G), stamp);
                channels[2].SetTo(new Scalar(color.R), stamp);
                Cv2.Merge(channels, image);
            }
            finally { foreach (var channel in channels) channel.Dispose(); }
        }
        if (Cv2.Norm(image, original, NormTypes.INF) <= 0d) return null;
        return await PutPngAsync(projectId, image, observation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyAssetReference?> ApplyColorFillAsync(
        Guid projectId, SurveyMapLayer layer, SurveyObservation observation,
        int pixelX, int pixelY, byte tolerance, SurveyColor color,
        CancellationToken cancellationToken = default)
    {
        var selected = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset : observation.SourceAsset);
        using var source = await ReadImageAsync(projectId, selected, ImreadModes.Unchanged, cancellationToken).ConfigureAwait(false);
        using var image = ToBgraForPainting(source);
        if (pixelX < 0 || pixelY < 0 || pixelX >= image.Width || pixelY >= image.Height) return null;
        var seed = image.At<Vec4b>(pixelY, pixelX);
        var replacement = new Vec4b(color.B, color.G, color.R, seed.Item3);
        if (seed.Item0 == replacement.Item0 && seed.Item1 == replacement.Item1 && seed.Item2 == replacement.Item2)
            return null;
        var visited = new bool[image.Rows * image.Cols];
        var pending = new Queue<(int X, int Y)>();
        pending.Enqueue((pixelX, pixelY));
        var changed = false;
        var brightness = double.IsFinite(layer.Brightness) ? layer.Brightness : 1d;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y) = pending.Dequeue();
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
            var index = (y * image.Width) + x;
            if (visited[index]) continue;
            visited[index] = true;
            var current = image.At<Vec4b>(y, x);
            var displayB = ToDisplayedByte(current.Item0, brightness);
            var displayG = ToDisplayedByte(current.Item1, brightness);
            var displayR = ToDisplayedByte(current.Item2, brightness);
            var seedB = ToDisplayedByte(seed.Item0, brightness);
            var seedG = ToDisplayedByte(seed.Item1, brightness);
            var seedR = ToDisplayedByte(seed.Item2, brightness);
            if (Math.Max(Math.Abs(displayB - seedB), Math.Max(Math.Abs(displayG - seedG), Math.Abs(displayR - seedR))) > tolerance)
                continue;
            if (current.Item0 != replacement.Item0 || current.Item1 != replacement.Item1 || current.Item2 != replacement.Item2)
            {
                image.Set(y, x, replacement);
                changed = true;
            }
            pending.Enqueue((x - 1, y));
            pending.Enqueue((x + 1, y));
            pending.Enqueue((x, y - 1));
            pending.Enqueue((x, y + 1));
        }
        if (!changed) return null;
        return await PutPngAsync(projectId, image, observation, cancellationToken).ConfigureAwait(false);
    }

    private static int ToDisplayedByte(byte value, double brightness) =>
        (int)Math.Clamp(Math.Round(value * brightness), 0d, 255d);

    private async Task<SurveyAssetReference> PutPngAsync(Guid projectId, Mat image, SurveyObservation observation, CancellationToken cancellationToken)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return await _assets.PutAsync(projectId, new SurveyEncodedFrame(bytes, ".png", "image/png",
            image.Width, image.Height, observation.Capture), cancellationToken).ConfigureAwait(false);
    }

    private static Mat ToBgraForPainting(Mat image)
    {
        var result = new Mat();
        if (image.Channels() == 4) image.CopyTo(result);
        else if (image.Channels() == 3) Cv2.CvtColor(image, result, ColorConversionCodes.BGR2BGRA);
        else if (image.Channels() == 1) Cv2.CvtColor(image, result, ColorConversionCodes.GRAY2BGRA);
        else if (image.Channels() == 2) Cv2.CvtColor(image, result, ColorConversionCodes.GRAY2BGRA);
        else throw new InvalidDataException($"Unsupported survey image channel count: {image.Channels()}.");
        return result;
    }
}
