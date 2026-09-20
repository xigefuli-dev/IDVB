using IDVBuff.Core.Contracts;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Detects the two in-game Gate icons from one preprocessed map viewport.
/// The detector owns its template and remembers the last successful scale.
/// </summary>
public sealed partial class GateTemplateDetector : IDisposable
{
    private readonly Mat _gateSource;
    private readonly IConfigProvider? _configProvider;
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

    /// <summary>
    /// Creates a detector that reads gate algorithm parameters from an
    /// <see cref="IConfigProvider"/> under the "detection.gate" section.
    /// Subscribes to <see cref="IConfigProvider.ConfigChanged"/> for hot-reload.
    /// </summary>
    public GateTemplateDetector(string gatePath, IConfigProvider configProvider)
        : this(gatePath)
    {
        _configProvider = configProvider;
        GateTemplateRules.ApplyConfig(configProvider);
        configProvider.ConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (_configProvider is null) return;
        GateTemplateRules.ApplyConfig(_configProvider);
    }

    public IReadOnlyList<GateDetection> Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds)
    {
        return Detect(
            liveMatchImage,
            viewportBounds,
            GateTemplateRules.ReferenceClientWidth,
            GateTemplateRules.MatchThreshold,
            searchContext: null).Gates;
    }

    public IReadOnlyList<GateDetection> Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds,
        double clientWidth)
    {
        return Detect(
            liveMatchImage,
            viewportBounds,
            clientWidth,
            GateTemplateRules.MatchThreshold,
            searchContext: null).Gates;
    }

    public IReadOnlyList<GateDetection> Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds,
        double clientWidth,
        double scoreThreshold)
    {
        return Detect(
            liveMatchImage,
            viewportBounds,
            clientWidth,
            scoreThreshold,
            searchContext: null).Gates;
    }

    public GateDetectionResult Detect(
        Mat liveMatchImage,
        MapScreenRect viewportBounds,
        double clientWidth,
        double scoreThreshold,
        GateSearchContext? searchContext,
        double physicalPixelsPerImagePixel = 1d)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scoreThreshold = double.IsFinite(scoreThreshold)
            ? Math.Clamp(scoreThreshold, 0d, 1d)
            : GateTemplateRules.MatchThreshold;
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

                    var width = Math.Max(12, (int)Math.Round(
                        _gateSource.Width * scale / physicalPixelsPerImagePixel));
                    var height = Math.Max(12, (int)Math.Round(
                        _gateSource.Height * scale / physicalPixelsPerImagePixel));
                    if (width >= liveMatchImage.Width || height >= liveMatchImage.Height)
                        continue;

                    var roi = BuildConfirmationRoi(region, width, height,
                        searchContext, liveMatchImage, viewportBounds,
                        physicalPixelsPerImagePixel);
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
                                viewportBounds.X + ((roi.X + location.X)
                                    * physicalPixelsPerImagePixel),
                                viewportBounds.Y + ((roi.Y + location.Y)
                                    * physicalPixelsPerImagePixel),
                                width * physicalPixelsPerImagePixel,
                                height * physicalPixelsPerImagePixel),
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

                var width = Math.Max(12, (int)Math.Round(
                    _gateSource.Width * scale / physicalPixelsPerImagePixel));
                var height = Math.Max(12, (int)Math.Round(
                    _gateSource.Height * scale / physicalPixelsPerImagePixel));
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
                            viewportBounds.X
                                + (location.X * physicalPixelsPerImagePixel),
                            viewportBounds.Y
                                + (location.Y * physicalPixelsPerImagePixel),
                            width * physicalPixelsPerImagePixel,
                            height * physicalPixelsPerImagePixel),
                    };
                    raw.Add(candidate);
                    scaleCandidates.Add(candidate);
                    var suppression = CreateSuppressionRect(location, scaled.Size(), output.Size());
                    Cv2.Rectangle(output, suppression, Scalar.All(-1d), -1);
                }

                // Dual-gate early exit requires two spatially distinct matches.
                if (searchContext.AllowDualGateEarlyExit
                    && scaleCandidates.Count >= 2
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

                // FullSearch needs enough scales before single-gate early exit.
                if (stopReason != GateSearchStopReason.DualGateEarlyExit
                    && searchContext.AllowSingleGateEarlyExit
                    && raw.Count > 0
                    && (searchContext.Mode == GateSearchMode.WarmScaleSearch
                        || searchContext.Mode == GateSearchMode.FullSearch))
                {
                    if (TrySingleGateEarlyExit(
                        searchContext,
                        raw,
                        scalesEvaluated,
                        out var singleExitReason))
                    {
                        stopReason = singleExitReason;
                        break;
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
        //
        // Global 0.5…1.5 fallback is intentionally omitted from the default
        // list: those large templates dominate MatchTemplate cost on ~1400px
        // viewports and almost never match real gate icons (~0.15–0.4).
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
        // Client-relative band only (no flat 0.5…1.5 global list).  Keep
        // enough samples for cold-start coverage while staying well under the
        // historical ~21-scale tax on large viewports.
        var clientRelativeScales = new[]
            {
                1d,
                GateTemplateRules.WarmScaleStart,
                GateTemplateRules.WarmScaleMaximum,
                0.7d,
                1.35d,
                0.55d,
                1.65d,
                2d,
                2.4d,
                2.8d,
            }
            .Select(factor => estimatedScale * factor);
        return warmScales
            .Concat(clientRelativeScales)
            .Select(scale => Math.Clamp(scale, 0.12d, 1.5d))
            .DistinctBy(scale => Math.Round(scale, 3))
            .ToArray();
    }

    private static bool TrySingleGateEarlyExit(
        GateSearchContext searchContext,
        List<GateDetection> raw,
        int scalesEvaluated,
        out GateSearchStopReason stopReason)
    {
        stopReason = GateSearchStopReason.Completed;
        if (searchContext.Mode == GateSearchMode.FullSearch
            && scalesEvaluated
                < GateTemplateRules.FullSearchMinScalesBeforeSingleGateExit)
        {
            return false;
        }

        var clusters = ClusterAcrossScales(raw);
        if (clusters.Count == 1)
        {
            var best = clusters[0]
                .OrderByDescending(c => c.Score)
                .First();
            if (best.Score < searchContext.SingleGateScoreThreshold)
                return false;

            if (searchContext.WarmScale is { } warmScale)
            {
                if (Math.Abs((best.Scale / warmScale) - 1d)
                    > searchContext.SingleGateScaleTolerance)
                {
                    return false;
                }
            }
            // FullSearch without an explicit warm scale: high single-cluster
            // score after MinScales is enough to stop burning remaining scales.

            stopReason = GateSearchStopReason.SingleGateWarmExit;
            return true;
        }

        if (clusters.Count >= 2
            && searchContext.Mode == GateSearchMode.WarmScaleSearch)
        {
            var ordered = clusters
                .Select(c => c.OrderByDescending(g => g.Score).First())
                .OrderByDescending(c => c.Score)
                .ToArray();
            if (ordered[0].Score - ordered[1].Score
                >= searchContext.AmbiguityScoreGap)
            {
                stopReason = GateSearchStopReason.SingleGateWarmExit;
                return true;
            }
        }

        return false;
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
}
/*
 * 文件职责：GateTemplateDetector。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
