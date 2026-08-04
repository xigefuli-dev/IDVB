using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>Controls how GateTemplateDetector enumerates scales and image regions.</summary>
public enum GateSearchMode
{
    /// <summary>Cold-start: warm scales + client-relative scales + global fallback.</summary>
    FullSearch,

    /// <summary>Only a narrow band around the remembered warm scale (~7 scales).</summary>
    WarmScaleSearch,

    /// <summary>Only search small ROIs around predicted gate positions (~3 scales).</summary>
    LocalConfirmationSearch,

    /// <summary>Single fixed scale from a locked gate-pair session — no scale search.</summary>
    LockedScale,
}

/// <summary>Why the search stopped — carried in GateDetectionResult for diagnostics.</summary>
public enum GateSearchStopReason
{
    Completed,
    DualGateEarlyExit,
    SingleGateWarmExit,
    BudgetExceeded,
    NoValidScale,
    InvalidSearchContext,
}

/// <summary>
/// Pure template-detection parameters. Contains no map / geometry / session types.
/// </summary>
public sealed class GateSearchContext
{
    public GateSearchMode Mode { get; init; } = GateSearchMode.FullSearch;

    // ── WarmScaleSearch ──────────────────────────────────────────
    public double? WarmScale { get; init; }

    // ── LocalConfirmationSearch ───────────────────────────────────
    public IReadOnlyList<MapScreenRect> PredictedGateRegions { get; init; } = [];
    public double? PredictedScale { get; init; }

    // ── LockedScale ──────────────────────────────────────────────
    public double? LockedScale { get; init; }

    // ── ROI ──────────────────────────────────────────────────────
    /// <summary>Multiplied by template size and added around each predicted region.</summary>
    public double LocalRoiTemplatePaddingFactor { get; init; } = 1.0;
    public int LocalRoiMinimumPaddingPixels { get; init; } = 24;
    public int MaximumExpectedMotionPixels { get; init; }

    // ── Budget ───────────────────────────────────────────────────
    /// <summary>Checked before each MatchTemplate call. Null = no budget.</summary>
    public int? TimeBudgetMilliseconds { get; set; }

    // ── Single-gate warm exit ────────────────────────────────────
    public bool AllowSingleGateEarlyExit { get; init; }
    public double SingleGateScoreThreshold { get; init; } =
        GateTemplateRules.EarlyExitScoreThreshold;
    public double SingleGateScaleTolerance { get; init; } =
        GateTemplateRules.SingleGateScaleTolerance;
    public double AmbiguityScoreGap { get; init; } =
        GateTemplateRules.SingleGateAmbiguityGap;
}

/// <summary>Full diagnostic result from one gate detection call.</summary>
public sealed class GateDetectionResult
{
    public IReadOnlyList<GateDetection> Gates { get; init; } = [];
    public IReadOnlyList<GateDetection> RawCandidates { get; init; } = [];
    public GateSearchMode SearchModeUsed { get; init; }
    public GateSearchStopReason StopReason { get; init; }
    public int ScalesEvaluated { get; init; }
    public int RegionsEvaluated { get; init; }
    public int MatchTemplateCalls { get; init; }
    public bool BudgetExceeded { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Detects the two in-game Gate icons from one preprocessed map viewport.
/// The detector owns its template and remembers the last successful scale.
/// </summary>
public sealed class GateTemplateDetector : IDisposable
{
    private readonly Mat _gateSource;
    private double? _warmScale;
    private bool _disposed;

    public GateTemplateDetector(string gatePath)
    {
        using var gate = Cv2.ImRead(gatePath, ImreadModes.Unchanged);
        if (gate.Empty())
            throw new InvalidOperationException($"无法读取门图标资源：{gatePath}");

        _gateSource = gate.Clone();
        using var gateEdges = CreateEdges(_gateSource);
        if (Cv2.CountNonZero(gateEdges) == 0)
            throw new InvalidOperationException("门图标资源无法生成有效的边缘模板。");
    }

    // ── Backward-compatible overload ──────────────────────────────────────

    public IReadOnlyList<GateDetection> Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds,
        double clientWidth = GateTemplateRules.ReferenceClientWidth,
        double scoreThreshold = MapRecognitionTuning.DefaultGateTemplateThreshold)
    {
        return Detect(
            liveMatchImage,
            viewportBounds,
            clientWidth,
            scoreThreshold,
            searchContext: null).Gates;
    }

    // ── New primary overload ──────────────────────────────────────────────

    public GateDetectionResult Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds,
        double clientWidth,
        double scoreThreshold,
        GateSearchContext? searchContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scoreThreshold = double.IsFinite(scoreThreshold)
            ? Math.Clamp(scoreThreshold, 0d, 1d)
            : MapRecognitionTuning.DefaultGateTemplateThreshold;
        searchContext ??= new GateSearchContext { Mode = GateSearchMode.FullSearch };

        var detectTimer = Stopwatch.StartNew();
        var raw = new List<GateDetection>();
        var scalesEvaluated = 0;
        var regionsEvaluated = 0;
        var matchTemplateCalls = 0;
        var stopReason = GateSearchStopReason.Completed;
        var budgetExceeded = false;
        var timeBudget = searchContext.TimeBudgetMilliseconds;

        if (searchContext.Mode == GateSearchMode.LocalConfirmationSearch
            && searchContext.PredictedGateRegions.Count > 0)
        {
            // ── Local confirmation: search each predicted ROI ──────────
            var scales = GetConfirmationScales(searchContext.PredictedScale);
            if (scales.Count == 0)
            {
                detectTimer.Stop();
                return new GateDetectionResult
                {
                    SearchModeUsed = searchContext.Mode,
                    StopReason = GateSearchStopReason.NoValidScale,
                    ElapsedMilliseconds = detectTimer.Elapsed.TotalMilliseconds,
                };
            }

            var budgetExpired = false;
            foreach (var region in searchContext.PredictedGateRegions)
            {
                if (!region.IsValid) continue;

                foreach (var scale in scales)
                {
                    if (timeBudget.HasValue
                        && detectTimer.Elapsed.TotalMilliseconds >= timeBudget.Value)
                    {
                        budgetExceeded = true;
                        stopReason = GateSearchStopReason.BudgetExceeded;
                        budgetExpired = true;
                        break;
                    }

                    var width = Math.Max(12, (int)Math.Round(_gateSource.Width * scale));
                    var height = Math.Max(12, (int)Math.Round(_gateSource.Height * scale));
                    if (width >= liveMatchImage.Width || height >= liveMatchImage.Height)
                        continue;

                    var roi = BuildConfirmationRoi(region, width, height, searchContext, liveMatchImage, viewportBounds);
                    scalesEvaluated++;
                    regionsEvaluated++;

                    using var scaledSource = new Mat();
                    Cv2.Resize(_gateSource, scaledSource, new Size(width, height),
                        0d, 0d,
                        scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
                    using var scaled = CreateMatchImage(scaledSource);
                    using var roiMat = new Mat(liveMatchImage, roi);
                    using var output = new Mat();
                    Cv2.MatchTemplate(roiMat, scaled, output, TemplateMatchModes.CCoeffNormed);
                    matchTemplateCalls++;

                    Cv2.MinMaxLoc(output, out _, out var score, out _, out var location);
                    if (score >= scoreThreshold)
                    {
                        raw.Add(new GateDetection
                        {
                            Score = score,
                            Scale = scale,
                            ScreenBounds = new MapScreenRect(
                                viewportBounds.X + roi.X + location.X,
                                viewportBounds.Y + roi.Y + location.Y,
                                width,
                                height),
                        });
                    }
                }
                if (budgetExpired) break;
            }
        }
        else
        {
            // ── Full or warm scale search over entire match image ──────
            var scales = GetScalesForMode(searchContext, clientWidth);
            if (scales.Count == 0)
            {
                detectTimer.Stop();
                return new GateDetectionResult
                {
                    SearchModeUsed = searchContext.Mode,
                    StopReason = GateSearchStopReason.NoValidScale,
                    ElapsedMilliseconds = detectTimer.Elapsed.TotalMilliseconds,
                };
            }

            foreach (var scale in scales)
            {
                if (timeBudget.HasValue
                    && detectTimer.Elapsed.TotalMilliseconds >= timeBudget.Value)
                {
                    budgetExceeded = true;
                    stopReason = GateSearchStopReason.BudgetExceeded;
                    break;
                }

                var width = Math.Max(12, (int)Math.Round(_gateSource.Width * scale));
                var height = Math.Max(12, (int)Math.Round(_gateSource.Height * scale));
                if (width >= liveMatchImage.Width || height >= liveMatchImage.Height)
                    continue;

                scalesEvaluated++;
                regionsEvaluated++;

                using var scaledSource = new Mat();
                Cv2.Resize(_gateSource, scaledSource, new Size(width, height),
                    0d, 0d,
                    scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
                using var scaled = CreateMatchImage(scaledSource);
                using var output = new Mat();
                Cv2.MatchTemplate(liveMatchImage, scaled, output, TemplateMatchModes.CCoeffNormed);
                matchTemplateCalls++;

                var scaleCandidates = new List<GateDetection>();
                for (var index = 0; index < 8; index++)
                {
                    Cv2.MinMaxLoc(output, out _, out var score, out _, out var location);
                    if (score < scoreThreshold)
                        break;

                    var candidate = new GateDetection
                    {
                        Score = score,
                        Scale = scale,
                        ScreenBounds = new MapScreenRect(
                            viewportBounds.X + location.X,
                            viewportBounds.Y + location.Y,
                            width,
                            height),
                    };
                    raw.Add(candidate);
                    scaleCandidates.Add(candidate);
                    var suppression = CreateSuppressionRect(location, scaled.Size(), output.Size());
                    Cv2.Rectangle(output, suppression, Scalar.All(-1d), -1);
                }

                // Dual-gate early exit: two high-score candidates in same scale.
                // Must verify they are spatially distinct (not same physical gate).
                if (scaleCandidates.Count >= 2
                    && scaleCandidates
                        .OrderByDescending(c => c.Score)
                        .Take(2)
                        .All(c => c.Score >= GateTemplateRules.EarlyExitScoreThreshold))
                {
                    var topTwo = scaleCandidates
                        .OrderByDescending(c => c.Score)
                        .Take(2)
                        .ToArray();
                    if (IntersectionOverUnion(topTwo[0].ScreenBounds, topTwo[1].ScreenBounds)
                        < GateTemplateRules.SpatialClusterIouThreshold)
                    {
                        stopReason = GateSearchStopReason.DualGateEarlyExit;
                        break;
                    }
                }

                // Single-gate warm early exit: check after each scale whether
                // we have a confident single-gate candidate that allows us to
                // stop early. DualGateEarlyExit always takes priority.
                if (stopReason != GateSearchStopReason.DualGateEarlyExit
                    && searchContext.Mode == GateSearchMode.WarmScaleSearch
                    && searchContext.AllowSingleGateEarlyExit
                    && raw.Count > 0)
                {
                    var clusters = ClusterAcrossScales(raw);
                    if (clusters.Count == 1)
                    {
                        var best = clusters[0]
                            .OrderByDescending(c => c.Score)
                            .First();
                        if (best.Score >= searchContext.SingleGateScoreThreshold
                            && searchContext.WarmScale is { } warmScale
                            && Math.Abs((best.Scale / warmScale) - 1d)
                                <= searchContext.SingleGateScaleTolerance)
                        {
                            stopReason = GateSearchStopReason.SingleGateWarmExit;
                            break;
                        }
                    }
                    else if (clusters.Count >= 2)
                    {
                        var ordered = clusters
                            .Select(c => c.OrderByDescending(g => g.Score).First())
                            .OrderByDescending(c => c.Score)
                            .ToArray();
                        if (ordered[0].Score - ordered[1].Score
                            >= searchContext.AmbiguityScoreGap)
                        {
                            stopReason = GateSearchStopReason.SingleGateWarmExit;
                            break;
                        }
                    }
                }
            }
        }

        detectTimer.Stop();

        // Cross-scale spatial clustering → deduplicated candidates.
        var clustered = ClusterAcrossScales(raw);
        var selected = SelectTopCandidates(clustered);

        MapLogCollector.Instance.Append(MapLogCategory.GateDetection, MapLogLevel.Info,
            $"门检测完成 · 找到 {selected.Count} 个候选门 · 模式 {searchContext.Mode} · 原因 {stopReason}",
            elapsedMs: detectTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["gateCount"] = selected.Count,
                ["threshold"] = scoreThreshold,
                ["mode"] = searchContext.Mode.ToString(),
                ["stopReason"] = stopReason.ToString(),
                ["scalesEvaluated"] = scalesEvaluated,
                ["matchTemplateCalls"] = matchTemplateCalls,
                ["budgetExceeded"] = budgetExceeded,
            });

        return new GateDetectionResult
        {
            Gates = selected,
            RawCandidates = raw,
            SearchModeUsed = searchContext.Mode,
            StopReason = stopReason,
            ScalesEvaluated = scalesEvaluated,
            RegionsEvaluated = regionsEvaluated,
            MatchTemplateCalls = matchTemplateCalls,
            BudgetExceeded = budgetExceeded,
            ElapsedMilliseconds = detectTimer.Elapsed.TotalMilliseconds,
        };
    }

    public bool HasWarmScale => _warmScale is { } warm && warm > 0d;

    public double? WarmScale => _warmScale;

    public void RememberSuccessfulScale(double scale)
    {
        if (double.IsFinite(scale) && scale > 0d)
            _warmScale = scale;
    }

    public void ResetSuccessfulScale() => _warmScale = null;

    public static Mat CreateEdges(Mat source)
    {
        using var gray = new Mat();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else if (source.Channels() == 4)
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0d);
        var edges = new Mat();
        Cv2.Canny(
            blurred,
            edges,
            GateTemplateRules.CannyLowThreshold,
            GateTemplateRules.CannyHighThreshold);
        return edges;
    }

    public static Mat CreateMatchImage(Mat source)
    {
        var gray = new Mat();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else if (source.Channels() == 4)
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    // ── Scale lists per mode ──────────────────────────────────────────────

    private IReadOnlyList<double> GetScalesForMode(
        GateSearchContext context, double clientWidth)
    {
        return context.Mode switch
        {
            GateSearchMode.WarmScaleSearch => GetWarmOnlyScales(context.WarmScale),
            GateSearchMode.LocalConfirmationSearch =>
                GetConfirmationScales(context.PredictedScale),
            GateSearchMode.LockedScale =>
                GetLockedOnlyScales(context.LockedScale),
            _ => GetFullScales(clientWidth),
        };
    }

    private IReadOnlyList<double> GetFullScales(double clientWidth)
    {
        var normalizedClientWidth = double.IsFinite(clientWidth) && clientWidth > 0d
            ? clientWidth
            : GateTemplateRules.ReferenceClientWidth;
        var estimatedScale = Math.Clamp(
            GateTemplateRules.ReferenceScale * normalizedClientWidth
                / GateTemplateRules.ReferenceClientWidth,
            0.12d,
            1.5d);
        // A remembered successful scale is stronger evidence than either
        // neighbouring warm samples or the client-width estimate.  Search
        // the centre first and expand outwards; otherwise an undersized
        // neighbour can produce two merely adequate matches and trigger the
        // dual-gate early exit before the exact scale is evaluated.
        IEnumerable<double> warmScales = _warmScale is { } warm
            ? new[]
            {
                warm,
                warm * (1d - GateTemplateRules.WarmScaleStep),
                warm * (1d + GateTemplateRules.WarmScaleStep),
                warm * GateTemplateRules.WarmScaleStart,
                warm * GateTemplateRules.WarmScaleMaximum,
            }
            : [];
        var clientRelativeScales = new[]
            {
                    1d, GateTemplateRules.WarmScaleStart,
                    GateTemplateRules.WarmScaleMaximum, 0.7d, 1.35d,
                0.55d, 1.65d, 2d, 2.4d, 2.8d,
            }
            .Select(factor => estimatedScale * factor);
        var globalFallback = Enumerable.Range(0, 11)
            .Select(index => 0.5d + (index * 0.1d));
        return warmScales
            .Concat(clientRelativeScales)
            .Concat(globalFallback)
            .Select(scale => Math.Clamp(scale, 0.12d, 1.5d))
            .DistinctBy(scale => Math.Round(scale, 3))
            .ToArray();
    }

    private IReadOnlyList<double> GetWarmOnlyScales(double? contextWarmScale)
    {
        // Caller-supplied warm scale (e.g. side-entrance multi-scale scan result)
        // takes priority; fall back to the detector's remembered scale from the
        // last successful detection. Cold-start with an explicit context scale
        // must work — the instance field is null until a detection succeeds.
        var warm = contextWarmScale is { } cw && double.IsFinite(cw) && cw > 0d
            ? cw
            : _warmScale is { } remembered && remembered > 0d
                ? remembered
                : 0d;
        if (warm <= 0d)
            return [];

        return new[]
        {
            warm * GateTemplateRules.WarmScaleStart,
            warm * 0.90d,
            warm * 0.95d,
            warm,
            warm * 1.05d,
            warm * 1.10d,
            warm * GateTemplateRules.WarmScaleMaximum,
        }
        .Select(s => Math.Clamp(s, 0.12d, 1.5d))
        .DistinctBy(s => Math.Round(s, 3))
        .ToArray();
    }

    private static IReadOnlyList<double> GetConfirmationScales(double? predictedScale)
    {
        if (predictedScale is not { } ps || ps <= 0d || !double.IsFinite(ps))
            return [];

        return new[]
        {
            ps * 0.95d,
            ps,
            ps * 1.05d,
        }
        .Select(s => Math.Clamp(s, 0.12d, 1.5d))
        .DistinctBy(s => Math.Round(s, 3))
        .ToArray();
    }

    private static IReadOnlyList<double> GetLockedOnlyScales(double? lockedScale)
    {
        if (lockedScale is not { } ls || ls <= 0d || !double.IsFinite(ls))
            return [];

        return new[] { Math.Clamp(ls, 0.12d, 1.5d) };
    }

    // ── ROI helpers ───────────────────────────────────────────────────────

    private static Rect BuildConfirmationRoi(
        MapScreenRect predictedRegion,
        int templateWidth,
        int templateHeight,
        GateSearchContext context,
        Mat matchImage,
        MapScreenRect viewportBounds)
    {
        var paddingX = Math.Max(
            context.LocalRoiMinimumPaddingPixels,
            (int)Math.Round(templateWidth * context.LocalRoiTemplatePaddingFactor)
                + context.MaximumExpectedMotionPixels);
        var paddingY = Math.Max(
            context.LocalRoiMinimumPaddingPixels,
            (int)Math.Round(templateHeight * context.LocalRoiTemplatePaddingFactor)
                + context.MaximumExpectedMotionPixels);

        // Convert absolute screen coordinates to viewport-local coordinates
        // for ROI construction. matchImage is the viewport crop.
        var localCenterX = predictedRegion.CenterX - viewportBounds.X;
        var localCenterY = predictedRegion.CenterY - viewportBounds.Y;
        var left = Math.Max(0, (int)Math.Round(localCenterX - (predictedRegion.Width / 2d) - paddingX));
        var top = Math.Max(0, (int)Math.Round(localCenterY - (predictedRegion.Height / 2d) - paddingY));
        var right = Math.Min(matchImage.Width,
            (int)Math.Round(localCenterX + (predictedRegion.Width / 2d) + paddingX));
        var bottom = Math.Min(matchImage.Height,
            (int)Math.Round(localCenterY + (predictedRegion.Height / 2d) + paddingY));

        if (right <= left || bottom <= top)
            return new Rect(0, 0, Math.Min(templateWidth, matchImage.Width),
                Math.Min(templateHeight, matchImage.Height));

        return new Rect(left, top, right - left, bottom - top);
    }

    // ── Cross-scale spatial clustering ────────────────────────────────────

    /// <summary>
    /// Groups raw candidates into spatially-clustered groups.
    /// Each cluster represents the same physical gate detected at adjacent scales.
    /// </summary>
    internal static List<List<GateDetection>> ClusterAcrossScales(
        List<GateDetection> raw)
    {
        if (raw.Count == 0) return [];

        // Sort by score descending; greedily assign each candidate to the first
        // existing cluster it overlaps (IoU >= threshold), or start a new cluster.
        var clusters = new List<List<GateDetection>>();
        foreach (var candidate in raw.OrderByDescending(c => c.Score))
        {
            var found = false;
            foreach (var cluster in clusters)
            {
                foreach (var member in cluster)
                {
                    if (IntersectionOverUnion(
                            candidate.ScreenBounds, member.ScreenBounds)
                        >= GateTemplateRules.SpatialClusterIouThreshold)
                    {
                        cluster.Add(candidate);
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            if (!found)
                clusters.Add([candidate]);
        }
        return clusters;
    }

    internal static IReadOnlyList<GateDetection> SelectTopCandidates(
        List<List<GateDetection>> clusters)
    {
        var selected = new List<GateDetection>();
        // Each cluster → single best candidate (highest score).
        var bestPerCluster = clusters
            .Select(cluster => cluster.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ToList();

        foreach (var candidate in bestPerCluster)
        {
            if (selected.Any(existing =>
                    IntersectionOverUnion(
                        existing.ScreenBounds, candidate.ScreenBounds)
                    >= GateTemplateRules.NmsIouThreshold))
            {
                continue;
            }
            selected.Add(candidate);
            if (selected.Count == GateTemplateRules.MaximumGateCandidates)
                break;
        }
        return selected;
    }

    // ── Static helpers ────────────────────────────────────────────────────

    private static Rect CreateSuppressionRect(Point location, Size template, Size output)
    {
        var left = Math.Max(0, location.X - (template.Width / 2));
        var top = Math.Max(0, location.Y - (template.Height / 2));
        var right = Math.Min(output.Width, location.X + template.Width);
        var bottom = Math.Min(output.Height, location.Y + template.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double IntersectionOverUnion(MapScreenRect left, MapScreenRect right)
    {
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersectionWidth = Math.Max(0d, intersectionRight - intersectionLeft);
        var intersectionHeight = Math.Max(0d, intersectionBottom - intersectionTop);
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0d ? 0d : intersection / union;
    }

    public static string ResolveGateAssetPath()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        if (File.Exists(deployed))
            return deployed;
        var workspace = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Gate.png"));
        if (File.Exists(workspace))
            return workspace;
        var current = Path.Combine(Environment.CurrentDirectory, "Assets", "Gate.png");
        return File.Exists(current) ? current : deployed;
    }

    // ── Reference gate icon measurement ────────────────────────────────

    /// <summary>
    /// Runs narrow-band multi-scale template matching in the reference image
    /// around <paramref name="anchorCenter"/> to measure the actual pixel size
    /// of the gate icon. Returns the matched template dimensions (in reference
    /// pixels), or null when no confident match is found.
    /// </summary>
    public static (double Width, double Height)? EstimateReferenceGateIconSize(
        Mat referenceImage,
        Point2d anchorCenter)
    {
        if (referenceImage.Empty())
            return null;

        using var matchImage = CreateMatchImage(referenceImage);
        using var gate = Cv2.ImRead(ResolveGateAssetPath(), ImreadModes.Unchanged);
        if (gate.Empty())
            return null;

        using var gateGray = CreateMatchImage(gate);
        var gateW = (double)gateGray.Width;
        var gateH = (double)gateGray.Height;

        // Narrow band of plausible icon scales on the reference image.
        // Reference images are rendered at a consistent resolution where
        // gate icons typically occupy ~2–5% of the image width.
        double[] scales = [0.16, 0.20, 0.24, 0.28, 0.32, 0.36, 0.40, 0.44];

        double bestScore = 0.5; // minimum confidence
        double bestWidth = 0d;
        double bestHeight = 0d;

        foreach (var scale in scales)
        {
            var width = Math.Max(12, (int)Math.Round(gateW * scale));
            var height = Math.Max(12, (int)Math.Round(gateH * scale));
            if (width >= matchImage.Width || height >= matchImage.Height)
                continue;

            var halfW = width / 2;
            var halfH = height / 2;
            var searchRadius = Math.Max(halfW, halfH) + 12; // small local search

            var roiX = Math.Max(0, (int)Math.Round(anchorCenter.X - searchRadius));
            var roiY = Math.Max(0, (int)Math.Round(anchorCenter.Y - searchRadius));
            var roiW = Math.Min(matchImage.Width - roiX, searchRadius * 2);
            var roiH = Math.Min(matchImage.Height - roiY, searchRadius * 2);
            if (roiW < width || roiH < height)
                continue;

            using var scaledGate = new Mat();
            Cv2.Resize(gateGray, scaledGate, new Size(width, height),
                0d, 0d,
                scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
            using var roi = new Mat(matchImage, new Rect(roiX, roiY, roiW, roiH));
            using var output = new Mat();
            Cv2.MatchTemplate(roi, scaledGate, output, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(output, out _, out var score, out _, out _);

            if (score > bestScore)
            {
                bestScore = score;
                bestWidth = width;
                bestHeight = height;
            }
        }

        if (bestWidth <= 0d || bestHeight <= 0d)
            return null;

        return (bestWidth, bestHeight);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gateSource.Dispose();
    }
}
