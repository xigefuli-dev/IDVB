using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

/// <summary>Whole-indicator states indexed by group key and canonical floor key.</summary>
internal sealed class FloorIndicatorTemplateRegistry
{
    public sealed record Group(string Key, double X, double Y, double Width, double Height,
        int PixelWidth, int PixelHeight,
        Dictionary<string, string> States, double ReferenceClientWidth = 1920);

    private static readonly Lazy<FloorIndicatorTemplateRegistry> Instance = new(() => new());
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mat> _images = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static FloorIndicatorTemplateRegistry? Available
    {
        get
        {
            try { return Instance.Value; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                return null;
            }
        }
    }

    public static void Prepare() => _ = Available;
    public static Group? Get(string key) => Available?._groups.GetValueOrDefault(key);

    private FloorIndicatorTemplateRegistry()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "FloorIndicators");
        var manifest = Path.Combine(root, "registry.json");
        if (!File.Exists(manifest)) return;
        foreach (var group in JsonSerializer.Deserialize<Group[]>(File.ReadAllText(manifest)) ?? [])
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.States.Count < 2
                || !double.IsFinite(group.X + group.Y + group.Width + group.Height)
                || group.X < 0 || group.Y < 0 || group.Width <= 0 || group.Height <= 0
                || group.X + group.Width > 1 || group.Y + group.Height > 1
                || group.PixelWidth < 16 || group.PixelHeight < 16
                || group.PixelWidth > 4096 || group.PixelHeight > 1024
                || !double.IsFinite(group.ReferenceClientWidth) || group.ReferenceClientWidth <= 0)
                throw new InvalidDataException("Invalid floor indicator group.");
            _groups.Add(group.Key, group);
            foreach (var (floor, filename) in group.States)
            {
                if (string.IsNullOrWhiteSpace(floor) || Path.GetFileName(filename) != filename)
                    throw new InvalidDataException("Invalid floor indicator state.");
                using var source = Cv2.ImRead(Path.Combine(root, filename), ImreadModes.Grayscale);
                if (source.Empty()) throw new InvalidDataException($"Missing indicator: {filename}");
                var normalized = new Mat();
                if (source.Width > group.PixelWidth || source.Height > group.PixelHeight)
                    throw new InvalidDataException("Indicator template exceeds search region.");
                Cv2.Resize(source, normalized, new Size(source.Width / 2, source.Height / 2),
                    0, 0, InterpolationFlags.Area);
                _images.Add(group.Key + "/" + floor, normalized);
            }
        }
    }

    public static Group? Resolve(IEnumerable<string> floors)
    {
        var keys = floors.ToHashSet(StringComparer.Ordinal);
        var registry = Available;
        if (registry is null) return null;
        var matches = registry._groups.Values.Where(g => keys.SetEquals(g.States.Keys)).ToArray();
        // Duplicate layouts for the same floor set need explicit selection, never registration-order wins.
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string? Recognize(Group group, Mat image, out double score, out double margin,
        double? templateScale = null)
    {
        score = margin = 0;
        try { return RecognizeCore(group, image, out score, out margin, templateScale); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            score = margin = 0;
            return null;
        }
    }

    private static string? RecognizeCore(Group group, Mat image, out double score, out double margin,
        double? templateScale)
    {
        score = margin = 0;
        var scale = templateScale ?? image.Width / (double)group.PixelWidth;
        if (image.Empty() || !double.IsFinite(scale) || scale <= 0) return null;
        using var gray = new Mat();
        if (image.Channels() == 1) image.CopyTo(gray);
        else Cv2.CvtColor(image, gray, image.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var normalized = new Mat();
        // Preserve aspect and search the complete header, not a guessed horizontal position.
        Cv2.Resize(gray, normalized, new Size(Math.Max(1, (int)Math.Round(image.Width / scale / 2)),
                Math.Max(1, (int)Math.Round(image.Height / scale / 2))),
            0, 0, InterpolationFlags.Area);
        using var result = new Mat();
        string? winner = null;
        double best = -1, second = -1;
        lock (Instance.Value._gate)
        {
            foreach (var floor in group.States.Keys)
            {
                var template = Instance.Value._images[group.Key + "/" + floor];
                if (normalized.Width < template.Width || normalized.Height < template.Height) continue;
                Cv2.MatchTemplate(normalized, template,
                    result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out double _, out double value);
                if (!double.IsFinite(value)) continue;
                if (value > best) { second = best; best = value; winner = floor; }
                else second = Math.Max(second, value);
            }
        }
        score = best;
        margin = best - second;
        return best >= 0.80 && margin >= 0.08 ? winner : null;
    }
}
