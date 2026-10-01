using IDVBuff.Core.Contracts;
using OpenCvSharp;
using System.Diagnostics;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Detects the two in-game Gate icons from one preprocessed map viewport.
/// The detector owns its template and remembers the last successful scale.
/// </summary>
public sealed partial class GateTemplateDetector : IDisposable
{
    private readonly Mat _gateSource;
    private readonly object _detectionGate = new();
    public GateAssetEvidence Asset { get; }
    private readonly IConfigProvider? _configProvider;
    private double? _warmScale;
    private bool _disposed;
    public GateTemplateDetector(string gatePath)
    {
        var bytes = File.ReadAllBytes(gatePath);
        using var gate = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (gate.Empty())
            throw new InvalidOperationException($"无法读取门图标资源：{gatePath}");

        using var gateEdges = CreateEdges(gate);
        if (Cv2.CountNonZero(gateEdges) == 0)
            throw new InvalidOperationException("门图标资源无法生成有效的边缘模板。");
        using var gray = CreateMatchImage(gate);
        Cv2.MeanStdDev(gray, out _, out var deviation);
        double? alphaMinimum = null, alphaMaximum = null;
        if (gate.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(gate, alpha, 3);
            Cv2.MinMaxLoc(alpha, out double minimum, out double maximum);
            alphaMinimum = minimum; alphaMaximum = maximum;
        }
        Asset = new GateAssetEvidence(Path.GetFileName(gatePath),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            gate.Width, gate.Height, gate.Channels(), deviation.Val0, alphaMinimum, alphaMaximum);
        _gateSource = gate.Clone();
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
        lock (_detectionGate)
        {
            if (!_disposed) GateTemplateRules.ApplyConfig(_configProvider);
        }
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
        lock (_detectionGate)
            return DetectCore(liveMatchImage, viewportBounds, clientWidth,
                scoreThreshold, searchContext, physicalPixelsPerImagePixel);
    }

    private GateDetectionResult DetectCore(Mat liveMatchImage, MapScreenRect viewportBounds,
        double clientWidth, double scoreThreshold, GateSearchContext? searchContext,
        double physicalPixelsPerImagePixel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scoreThreshold = double.IsFinite(scoreThreshold)
            ? Math.Clamp(scoreThreshold, 0d, 1d)
            : GateTemplateRules.MatchThreshold;
        searchContext ??= new GateSearchContext { Mode = GateSearchMode.FullSearch };
        if (liveMatchImage.Empty() || !viewportBounds.IsValid
            || !double.IsFinite(viewportBounds.X) || !double.IsFinite(viewportBounds.Y)
            || !double.IsFinite(viewportBounds.Width) || !double.IsFinite(viewportBounds.Height)
            || !double.IsFinite(clientWidth) || clientWidth <= 0
            || !double.IsFinite(physicalPixelsPerImagePixel) || physicalPixelsPerImagePixel <= 0)
            return new GateDetectionResult { Asset = Asset, StopReason = GateSearchStopReason.InvalidSearchContext };

        var detectTimer = Stopwatch.StartNew();
        var raw = new List<GateDetection>();
        var evidence = new List<GateScaleEvidence>();
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
                    if (searchContext.CancellationToken.IsCancellationRequested
                        || ScanExecutionContext.Current is { CanCompute: false }
                        || (timeBudget.HasValue
                            && detectTimer.Elapsed.TotalMilliseconds >= timeBudget.Value))
                    {
                        budgetExceeded = true;
                        stopReason = searchContext.CancellationToken.IsCancellationRequested
                            ? GateSearchStopReason.Canceled : GateSearchStopReason.BudgetExceeded;
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
                    evidence.Add(new(scale, width, height, physicalPixelsPerImagePixel,
                        roi.X, roi.Y, double.IsFinite(score) ? score : null, location.X, location.Y));
                    if (double.IsFinite(score) && score >= scoreThreshold)
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
                if (searchContext.CancellationToken.IsCancellationRequested
                    || ScanExecutionContext.Current is { CanCompute: false }
                    || (timeBudget.HasValue
                        && detectTimer.Elapsed.TotalMilliseconds >= timeBudget.Value))
                {
                    budgetExceeded = true;
                    stopReason = searchContext.CancellationToken.IsCancellationRequested
                        ? GateSearchStopReason.Canceled : GateSearchStopReason.BudgetExceeded;
                    break;
                }

                var width = Math.Max((int)Math.Ceiling(12 / physicalPixelsPerImagePixel), (int)Math.Round(
                    _gateSource.Width * scale / physicalPixelsPerImagePixel));
                var height = Math.Max((int)Math.Ceiling(12 / physicalPixelsPerImagePixel), (int)Math.Round(
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
                Cv2.MinMaxLoc(output, out _, out var maximum, out _, out var maximumLocation);
                evidence.Add(new(scale, width, height, physicalPixelsPerImagePixel,
                    0, 0, double.IsFinite(maximum) ? maximum : null, maximumLocation.X, maximumLocation.Y));

                var scaleCandidates = new List<GateDetection>();
                for (var index = 0; index < 8; index++)
                {
                    Cv2.MinMaxLoc(output, out _, out var score, out _, out var location);
                    if (!double.IsFinite(score) || score < scoreThreshold)
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
        if (searchContext.CancellationToken.IsCancellationRequested)
        {
            selected = [];
            stopReason = GateSearchStopReason.Canceled;
            budgetExceeded = false;
        }
        else if (budgetExceeded || searchContext.TimeBudgetMilliseconds is { } finalBudget
            && detectTimer.Elapsed.TotalMilliseconds >= finalBudget)
        {
            selected = [];
            stopReason = GateSearchStopReason.BudgetExceeded;
            budgetExceeded = true;
        }

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
                ["asset"] = Asset,
                ["scaleEvidence"] = evidence,
                ["rawCount"] = raw.Count,
                ["clusterCount"] = clustered.Count,
                ["viewport"] = viewportBounds,
                ["clientWidth"] = clientWidth,
                ["physicalPixelsPerImagePixel"] = physicalPixelsPerImagePixel,
            });

        return new GateDetectionResult
        {
            Asset = Asset,
            ScaleEvidence = evidence,
            ClusterCount = clustered.Count,
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


}
/*
 * 文件职责：GateTemplateDetector。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
