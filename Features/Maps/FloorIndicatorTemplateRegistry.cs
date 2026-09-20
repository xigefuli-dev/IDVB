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

    public const double DefaultMinScore = 0.80d;
    // Accept the highest-scoring floor once it passes the absolute score gate.
    // Similar indicator states must not prevent switching away from a stale floor.
    public const double DefaultMinMargin = 0d;

    public static string? Recognize(Group group, Mat image, out double score, out double margin,
        double? templateScale = null)
    {
        var result = RecognizeDetailed(group, image, templateScale);
        score = result.BestScore;
        margin = result.Margin;
        return result.DetectedFloor;
    }

    public static FloorIndicatorMatchResult RecognizeDetailed(
        Group group,
        Mat image,
        double? templateScale = null,
        double minScore = DefaultMinScore,
        double minMargin = DefaultMinMargin)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (image is null || image.Empty())
        {
            return new FloorIndicatorMatchResult(
                Succeeded: false,
                DetectedFloor: null,
                BestScore: 0d,
                BestFloor: null,
                SecondScore: 0d,
                SecondFloor: null,
                Margin: 0d,
                MinScoreThreshold: minScore,
                MinMarginThreshold: minMargin,
                CandidateScores: new Dictionary<string, double>(),
                RejectionReason: "EmptyIndicatorImage",
                FailureDescription: "指示器图像为空或未提供有效图像数据。",
                Scale: 0d,
                IndicatorWidth: 0,
                IndicatorHeight: 0,
                NormalizedWidth: 0,
                NormalizedHeight: 0,
                MatchMilliseconds: timer.Elapsed.TotalMilliseconds);
        }

        var scale = templateScale ?? image.Width / (double)group.PixelWidth;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return new FloorIndicatorMatchResult(
                Succeeded: false,
                DetectedFloor: null,
                BestScore: 0d,
                BestFloor: null,
                SecondScore: 0d,
                SecondFloor: null,
                Margin: 0d,
                MinScoreThreshold: minScore,
                MinMarginThreshold: minMargin,
                CandidateScores: new Dictionary<string, double>(),
                RejectionReason: "InvalidTemplateScale",
                FailureDescription: $"指示器缩放比例无效：scale={scale}。",
                Scale: scale,
                IndicatorWidth: image.Width,
                IndicatorHeight: image.Height,
                NormalizedWidth: 0,
                NormalizedHeight: 0,
                MatchMilliseconds: timer.Elapsed.TotalMilliseconds);
        }

        try
        {
            using var gray = new Mat();
            if (image.Channels() == 1)
                image.CopyTo(gray);
            else
                Cv2.CvtColor(image, gray, image.Channels() == 4
                    ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);

            var normW = Math.Max(1, (int)Math.Round(image.Width / scale / 2));
            var normH = Math.Max(1, (int)Math.Round(image.Height / scale / 2));
            using var normalized = new Mat();
            Cv2.Resize(gray, normalized, new Size(normW, normH), 0, 0, InterpolationFlags.Area);

            using var result = new Mat();
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            string? bestFloor = null;
            string? secondFloor = null;
            double best = -1, second = -1;
            var anyTemplateMatched = false;

            lock (Instance.Value._gate)
            {
                foreach (var floor in group.States.Keys)
                {
                    var template = Instance.Value._images[group.Key + "/" + floor];
                    if (normalized.Width < template.Width || normalized.Height < template.Height)
                    {
                        scores[floor] = -1d;
                        continue;
                    }
                    anyTemplateMatched = true;
                    Cv2.MatchTemplate(normalized, template, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out double value);
                    if (!double.IsFinite(value))
                    {
                        scores[floor] = -1d;
                        continue;
                    }
                    scores[floor] = value;
                    if (value > best)
                    {
                        second = best;
                        secondFloor = bestFloor;
                        best = value;
                        bestFloor = floor;
                    }
                    else if (value > second)
                    {
                        second = value;
                        secondFloor = floor;
                    }
                }
            }

            timer.Stop();
            var elapsedMs = timer.Elapsed.TotalMilliseconds;

            if (!anyTemplateMatched)
            {
                return new FloorIndicatorMatchResult(
                    Succeeded: false,
                    DetectedFloor: null,
                    BestScore: 0d,
                    BestFloor: null,
                    SecondScore: 0d,
                    SecondFloor: null,
                    Margin: 0d,
                    MinScoreThreshold: minScore,
                    MinMarginThreshold: minMargin,
                    CandidateScores: scores,
                    RejectionReason: "SearchRegionSmallerThanTemplate",
                    FailureDescription: $"指示器归一化搜索尺寸 ({normW}x{normH}) 小于模板尺寸。",
                    Scale: scale,
                    IndicatorWidth: image.Width,
                    IndicatorHeight: image.Height,
                    NormalizedWidth: normW,
                    NormalizedHeight: normH,
                    MatchMilliseconds: elapsedMs);
            }

            if (best < 0)
            {
                return new FloorIndicatorMatchResult(
                    Succeeded: false,
                    DetectedFloor: null,
                    BestScore: 0d,
                    BestFloor: null,
                    SecondScore: 0d,
                    SecondFloor: null,
                    Margin: 0d,
                    MinScoreThreshold: minScore,
                    MinMarginThreshold: minMargin,
                    CandidateScores: scores,
                    RejectionReason: "NoFiniteScores",
                    FailureDescription: "模板匹配未产生有效有限分值。",
                    Scale: scale,
                    IndicatorWidth: image.Width,
                    IndicatorHeight: image.Height,
                    NormalizedWidth: normW,
                    NormalizedHeight: normH,
                    MatchMilliseconds: elapsedMs);
            }

            var safeSecond = second >= 0 ? second : 0d;
            var margin = best - safeSecond;

            if (best < minScore)
            {
                return new FloorIndicatorMatchResult(
                    Succeeded: false,
                    DetectedFloor: null,
                    BestScore: best,
                    BestFloor: bestFloor,
                    SecondScore: safeSecond,
                    SecondFloor: secondFloor,
                    Margin: margin,
                    MinScoreThreshold: minScore,
                    MinMarginThreshold: minMargin,
                    CandidateScores: scores,
                    RejectionReason: "ScoreBelowThreshold",
                    FailureDescription: $"最高得分 {best:F3} 低于阈值 {minScore:F3}（最佳候选: {bestFloor}）。",
                    Scale: scale,
                    IndicatorWidth: image.Width,
                    IndicatorHeight: image.Height,
                    NormalizedWidth: normW,
                    NormalizedHeight: normH,
                    MatchMilliseconds: elapsedMs);
            }

            if (margin < minMargin)
            {
                return new FloorIndicatorMatchResult(
                    Succeeded: false,
                    DetectedFloor: null,
                    BestScore: best,
                    BestFloor: bestFloor,
                    SecondScore: safeSecond,
                    SecondFloor: secondFloor,
                    Margin: margin,
                    MinScoreThreshold: minScore,
                    MinMarginThreshold: minMargin,
                    CandidateScores: scores,
                    RejectionReason: "MarginBelowThreshold",
                    FailureDescription: $"最佳候选 '{bestFloor}' ({best:F3}) 与次佳候选 '{secondFloor}' ({safeSecond:F3}) 区分度 {margin:F3} 低于阈值 {minMargin:F3}，存在歧义。",
                    Scale: scale,
                    IndicatorWidth: image.Width,
                    IndicatorHeight: image.Height,
                    NormalizedWidth: normW,
                    NormalizedHeight: normH,
                    MatchMilliseconds: elapsedMs);
            }

            return new FloorIndicatorMatchResult(
                Succeeded: true,
                DetectedFloor: bestFloor,
                BestScore: best,
                BestFloor: bestFloor,
                SecondScore: safeSecond,
                SecondFloor: secondFloor,
                Margin: margin,
                MinScoreThreshold: minScore,
                MinMarginThreshold: minMargin,
                CandidateScores: scores,
                RejectionReason: "None",
                FailureDescription: string.Empty,
                Scale: scale,
                IndicatorWidth: image.Width,
                IndicatorHeight: image.Height,
                NormalizedWidth: normW,
                NormalizedHeight: normH,
                MatchMilliseconds: elapsedMs);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return new FloorIndicatorMatchResult(
                Succeeded: false,
                DetectedFloor: null,
                BestScore: 0d,
                BestFloor: null,
                SecondScore: 0d,
                SecondFloor: null,
                Margin: 0d,
                MinScoreThreshold: minScore,
                MinMarginThreshold: minMargin,
                CandidateScores: new Dictionary<string, double>(),
                RejectionReason: "TemplateMatchException",
                FailureDescription: $"模板匹配异常：{exception.Message}",
                Scale: scale,
                IndicatorWidth: image.Width,
                IndicatorHeight: image.Height,
                NormalizedWidth: 0,
                NormalizedHeight: 0,
                MatchMilliseconds: timer.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>楼层指示器模板匹配的结构化详细结果。</summary>
public sealed record FloorIndicatorMatchResult(
    bool Succeeded,
    string? DetectedFloor,
    double BestScore,
    string? BestFloor,
    double SecondScore,
    string? SecondFloor,
    double Margin,
    double MinScoreThreshold,
    double MinMarginThreshold,
    IReadOnlyDictionary<string, double> CandidateScores,
    string RejectionReason,
    string FailureDescription,
    double Scale,
    int IndicatorWidth,
    int IndicatorHeight,
    int NormalizedWidth,
    int NormalizedHeight,
    double MatchMilliseconds);
