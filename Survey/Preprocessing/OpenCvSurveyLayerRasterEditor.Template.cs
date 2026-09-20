using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Preprocessing.OpenCv;

public sealed partial class OpenCvSurveyLayerRasterEditor
{
    public async Task<SurveyAssetReference> ApplyColorTemplateAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        IReadOnlyList<SurveyColorTemplateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            throw new ArgumentException("A color template must contain at least one entry.", nameof(entries));

        var selected = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
        using var source = await ReadImageAsync(
            projectId,
            selected,
            ImreadModes.Unchanged,
            cancellationToken).ConfigureAwait(false);
        using var bgra = ToBgra(source);

        var width = bgra.Width;
        var height = bgra.Height;
        var pixelCount = checked(width * height);
        var lightness = new float[pixelCount];
        var lab = new float[pixelCount * 3];
        ConvertToLab(bgra, lightness, lab, cancellationToken);

        var (edge, detail, complexity) = ComputeStructureFeatures(lightness, width, height);
        var paletteLab = entries
            .Select(entry => RgbToLab(entry.R, entry.G, entry.B))
            .ToArray();
        using var output = new Mat(height, width, MatType.CV_8UC4);
        RenderTemplatePixels(
            bgra,
            output,
            lab,
            edge,
            detail,
            complexity,
            entries,
            paletteLab,
            cancellationToken);

        Cv2.ImEncode(".png", output, out var bytes);
        return await _assets.PutAsync(
            projectId,
            new SurveyEncodedFrame(
                bytes,
                ".png",
                "image/png",
                width,
                height,
                observation.Capture),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ConvertToLab(
        Mat bgra,
        float[] lightness,
        float[] lab,
        CancellationToken cancellationToken)
    {
        var rows = bgra.AsRows<Vec4b>();
        var width = bgra.Width;
        var height = bgra.Height;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[y];
            for (var x = 0; x < width; x++)
            {
                var pixel = row[x];
                var converted = RgbToLab(pixel.Item2, pixel.Item1, pixel.Item0);
                var index = (y * width) + x;
                var offset = index * 3;
                lab[offset] = converted.L;
                lab[offset + 1] = converted.A;
                lab[offset + 2] = converted.B;
                lightness[index] = converted.L;
            }
        }
    }

    private static void RenderTemplatePixels(
        Mat source,
        Mat output,
        float[] lab,
        float[] edge,
        float[] detail,
        float[] complexity,
        IReadOnlyList<SurveyColorTemplateEntry> entries,
        IReadOnlyList<(float L, float A, float B)> paletteLab,
        CancellationToken cancellationToken)
    {
        var sourceRows = source.AsRows<Vec4b>();
        var outputRows = output.AsRows<Vec4b>();
        var width = source.Width;
        var height = source.Height;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = sourceRows[y];
            var outputRow = outputRows[y];
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var labOffset = index * 3;
                var best = 0;
                var bestCost = double.PositiveInfinity;
                for (var paletteIndex = 0; paletteIndex < entries.Count; paletteIndex++)
                {
                    var candidate = paletteLab[paletteIndex];
                    var dl = lab[labOffset] - candidate.L;
                    var da = lab[labOffset + 1] - candidate.A;
                    var db = lab[labOffset + 2] - candidate.B;
                    var cost = Math.Sqrt((dl * dl) + (da * da) + (db * db))
                        + TypePenalty(entries[paletteIndex].Type, edge[index], detail[index], complexity[index]);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = paletteIndex;
                    }
                }

                var original = sourceRow[x];
                if (original.Item3 == 0)
                {
                    outputRow[x] = original;
                    continue;
                }

                var selectedColor = entries[best];
                outputRow[x] = new Vec4b(
                    selectedColor.B,
                    selectedColor.G,
                    selectedColor.R,
                    original.Item3);
            }
        }
    }

    private static (float[] Edge, float[] Detail, float[] Complexity) ComputeStructureFeatures(
        float[] lightness,
        int width,
        int height)
    {
        var count = lightness.Length;
        var edge = new float[count];
        var detail = new float[count];
        var complexity = new float[count];
        var gradient = new float[count];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var center = lightness[index];
                var left = lightness[(y * width) + Math.Max(0, x - 1)];
                var right = lightness[(y * width) + Math.Min(width - 1, x + 1)];
                var up = lightness[(Math.Max(0, y - 1) * width) + x];
                var down = lightness[(Math.Min(height - 1, y + 1) * width) + x];
                var hEdge = MathF.Max(MathF.Abs(center - left), MathF.Abs(center - right));
                var vEdge = MathF.Max(MathF.Abs(center - up), MathF.Abs(center - down));
                var gMax = MathF.Max(hEdge, vEdge);
                gradient[index] = gMax;

                var sum = 0f;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        var sampleY = Math.Clamp(y + dy, 0, height - 1);
                        var sampleX = Math.Clamp(x + dx, 0, width - 1);
                        sum += MathF.Abs(center - lightness[(sampleY * width) + sampleX]);
                    }
                }
                detail[index] = sum / 8f;
            }
        }

        var edgeScale = Math.Max(Percentile(gradient, 0.92f), 4f);
        var detailScale = Math.Max(Percentile(detail, 0.92f), 3f);
        for (var index = 0; index < count; index++)
        {
            edge[index] = Math.Clamp(gradient[index] / edgeScale, 0f, 1f);
            detail[index] = Math.Clamp(detail[index] / detailScale, 0f, 1f);
            var hEdge = MathF.Max(
                MathF.Abs(lightness[(index / width) * width + Math.Max(0, (index % width) - 1)] - lightness[index]),
                MathF.Abs(lightness[(index / width) * width + Math.Min(width - 1, (index % width) + 1)] - lightness[index]));
            var vEdge = MathF.Max(
                MathF.Abs(lightness[Math.Max(0, (index / width) - 1) * width + (index % width)] - lightness[index]),
                MathF.Abs(lightness[Math.Min(height - 1, (index / width) + 1) * width + (index % width)] - lightness[index]));
            var balance = Math.Clamp(Math.Min(hEdge, vEdge) / (Math.Max(hEdge, vEdge) + 0.0001f), 0f, 1f);
            complexity[index] = Math.Clamp(
                (0.55f * detail[index]) + (0.45f * balance * edge[index]),
                0f,
                1f);
        }
        return (edge, detail, complexity);
    }

    private static float Percentile(float[] values, float percentile)
    {
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        var index = Math.Clamp(
            (int)Math.Round((sorted.Length - 1) * percentile),
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static double TypePenalty(
        SurveyTemplateColorType type,
        float edge,
        float detail,
        float complexity) => type switch
        {
            SurveyTemplateColorType.Fill => (9d * edge) + (5d * detail),
            SurveyTemplateColorType.Outline =>
                (8d * (1d - edge)) + (5d * (1d - detail)) + (3d * complexity),
            SurveyTemplateColorType.Icon =>
                (3d * (1d - edge)) + (7d * (1d - detail)) + (6d * (1d - complexity)),
            _ => 0d
        };

    private static (float L, float A, float B) RgbToLab(byte r, byte g, byte b)
    {
        var red = SrgbToLinear(r / 255f);
        var green = SrgbToLinear(g / 255f);
        var blue = SrgbToLinear(b / 255f);

        var x = ((red * 0.4124564f) + (green * 0.3575761f) + (blue * 0.1804375f)) / 0.95047f;
        var y = (red * 0.2126729f) + (green * 0.7151522f) + (blue * 0.0721750f);
        var z = ((red * 0.0193339f) + (green * 0.1191920f) + (blue * 0.9503041f)) / 1.08883f;
        var delta = 6f / 29f;
        var fx = LabPivot(x, delta);
        var fy = LabPivot(y, delta);
        var fz = LabPivot(z, delta);
        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    private static float SrgbToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float LabPivot(float value, float delta) => value > delta * delta * delta
        ? MathF.Pow(value, 1f / 3f)
        : (value / (3f * delta * delta)) + (4f / 29f);

    private static Mat ToBgra(Mat image)
    {
        var result = new Mat();
        if (image.Channels() == 4)
            image.CopyTo(result);
        else if (image.Channels() == 3)
            Cv2.CvtColor(image, result, ColorConversionCodes.BGR2BGRA);
        else if (image.Channels() == 1)
            Cv2.CvtColor(image, result, ColorConversionCodes.GRAY2BGRA);
        else if (image.Channels() == 2)
        {
            var channels = Cv2.Split(image);
            try
            {
                Cv2.Merge([channels[0], channels[0], channels[0], channels[1]], result);
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }
        }
        else
            throw new InvalidDataException($"Unsupported survey color template channel count: {image.Channels()}.");
        return result;
    }
}
