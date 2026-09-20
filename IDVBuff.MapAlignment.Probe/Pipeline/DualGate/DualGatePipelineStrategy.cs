using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Output;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline.DualGate;

/// <summary>
/// 双门对齐管线：门检测 → 几何指纹排名 → 可选结构配准复核。
/// 对应原 Program.cs 中的 RunAsync / StatsAsync 命令。
/// </summary>
public sealed class DualGatePipelineStrategy : IPipelineStrategy
{
    public string StrategyName => "dual-gate";

    public async Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        // ── 阶段 1：加载图片 ──
        using var fullImage = Cv2.ImRead(context.ImagePath, ImreadModes.Unchanged);
        if (fullImage.Empty())
            return Fail("无法读取游戏截图。", totalTimer);
        var loadMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        // ── 阶段 2：裁剪视口 ──
        MapScreenRect viewport;
        Mat screenshot;
        if (!context.UseFullFrame && context.ViewportRegion is not null)
        {
            var region = context.ViewportRegion;
            var marginW = region.Width * context.ViewportMargin;
            var marginH = region.Height * context.ViewportMargin;
            var expanded = new NormalizedRectangle
            {
                X = Math.Max(0d, region.X - marginW),
                Y = Math.Max(0d, region.Y - marginH),
                Width = Math.Min(1d, region.Width + marginW * 2d),
                Height = Math.Min(1d, region.Height + marginH * 2d)
            };
            var left = Math.Clamp(
                (int)Math.Floor(expanded.X * fullImage.Width),
                0, Math.Max(0, fullImage.Width - 1));
            var top = Math.Clamp(
                (int)Math.Floor(expanded.Y * fullImage.Height),
                0, Math.Max(0, fullImage.Height - 1));
            var right = Math.Clamp(
                (int)Math.Ceiling((expanded.X + expanded.Width) * fullImage.Width),
                left + 1, fullImage.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling((expanded.Y + expanded.Height) * fullImage.Height),
                top + 1, fullImage.Height);
            screenshot = new Mat(fullImage, new Rect(left, top, right - left, bottom - top));
            viewport = new MapScreenRect(0d, 0d, screenshot.Width, screenshot.Height);
        }
        else
        {
            screenshot = fullImage;
            viewport = new MapScreenRect(0d, 0d, screenshot.Width, screenshot.Height);
        }
        ct.ThrowIfCancellationRequested();

        // ── 阶段 3：门检测 ──
        var gatePath = string.IsNullOrWhiteSpace(context.GateTemplatePath)
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png")
            : context.GateTemplatePath;
        using var detector = new GateTemplateDetector(gatePath);

        phase.Restart();
        using var matchImage = GateTemplateDetector.CreateMatchImage(screenshot);
        var gateCreateMatchMs = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        var gates = detector.Detect(matchImage, viewport, context.ClientWidth, context.GateThreshold);
        var gateDetectMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        // ── 阶段 4：加载地图目录 + 构建指纹 ──
        phase.Restart();
        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        var catalogMs = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        foreach (var map in maps)
            map.NormalizeRecognition();
        var fingerprints = maps
            .Select(BuildFingerprint)
            .Where(f => f is not null)
            .Cast<MapGeometryFingerprint>()
            .ToArray();
        var fingerprintMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        if (fingerprints.Length == 0)
            return Fail("没有可识别的地图。", totalTimer);

        // ── 阶段 5：几何排名 ──
        phase.Restart();
        var ranked = MapCvRecognitionScript.RankGeometry(fingerprints, gates, viewport)
            .Take(context.TopCount)
            .ToArray();
        var geometryMs = phase.Elapsed.TotalMilliseconds;

        // ── 阶段 6：构建候选列表 + 可选结构配准 ──
        var candidates = new List<CandidateInfo>();
        double structureWallMs = 0d;
        double referenceLoadMs = 0d;
        ProbeResult? finalResult = null;

        foreach (var candidate in ranked)
        {
            var map = candidate.Fingerprint.Map;
            phase.Restart();
            var recognitionPath = repository.GetFloorOneRecognitionPath(map);
            using var reference = !File.Exists(recognitionPath)
                ? null
                : Cv2.ImRead(recognitionPath, ImreadModes.Unchanged);
            referenceLoadMs = phase.Elapsed.TotalMilliseconds;
            ct.ThrowIfCancellationRequested();

            StructureCandidateInfo? structureInfo = null;

            if (context.EnableStructure
                && reference is not null
                && !reference.Empty())
            {
                var structSw = Stopwatch.StartNew();
                var preprocessor = new MapStructurePreprocessor();
                var tuning = new MapStructureRegistrationTuning
                {
                    SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
                    EnableEccRefinement = context.EnableEcc,
                    TopCandidateCount = context.TopCandidates
                };

                var downscaleFactor = EffectiveDownscaleFactor(context.DownscaleFactor);
                using var liveForProcess = DownscaleImage(screenshot, downscaleFactor, out _);

                var refPrepHolder = Stopwatch.StartNew();
                var preparedReference = preprocessor.ProcessCachedReference(
                    reference, recognitionPath, out var _, out var cacheHit);
                double refPrepMs = refPrepHolder.Elapsed.TotalMilliseconds;

                refPrepHolder.Restart();
                using var preparedLive = preprocessor.ProcessLiveRoi(
                    liveForProcess,
                    ignoreRegions: null,
                    dynamicIgnoreRegions: null,
                    generateVisibleMask: false);
                double livePrepMs = refPrepHolder.Elapsed.TotalMilliseconds;

                refPrepHolder.Restart();
                preparedReference.GetOrCreateReferenceDistanceMap();
                preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
                double distMapMs = refPrepHolder.Elapsed.TotalMilliseconds;

                var uniformScale = (candidate.EstimatedScaleX + candidate.EstimatedScaleY) / 2d;
                var lockedScale = uniformScale * downscaleFactor;
                var scaledViewport = new MapScreenRect(0d, 0d, liveForProcess.Width, liveForProcess.Height);

                var registrar = new MapStructureRegistrar(preprocessor);
                var result = registrar.Register(new MapStructureRegistrationRequest
                {
                    ReferenceImage = reference,
                    LiveRoi = liveForProcess,
                    ViewportBounds = scaledViewport,
                    LockedTransform = new MapOverlayTransform
                    {
                        ScaleX = lockedScale,
                        ScaleY = lockedScale,
                        OffsetX = 0d,
                        OffsetY = 0d,
                        ReferenceWidth = reference.Width,
                        ReferenceHeight = reference.Height,
                        AlignmentMode = MapOverlayAlignmentMode.Uniform
                    },
                    Tuning = tuning,
                    ScaleSearchPolicy = MapScaleSearchPolicy.Search,
                    RestrictSearchToLockedTransform = true,
                    TrackingMode = true,
                    ForceBestCandidate = context.ForceBestCandidate,
                    PreparedReference = preparedReference,
                    PreparedLive = preparedLive
                });

                structureWallMs = structSw.Elapsed.TotalMilliseconds;
                structureInfo = new StructureCandidateInfo
                {
                    Accepted = result.Accepted,
                    Confidence = result.Confidence,
                    Scale = result.Transform?.ScaleX / downscaleFactor,
                    OffsetX = result.Transform?.OffsetX / downscaleFactor,
                    OffsetY = result.Transform?.OffsetY / downscaleFactor,
                    BestScore = result.BestScore,
                    CandidateMargin = result.CandidateMargin,
                    Rejection = result.RejectionReason.ToString(),
                    WallMs = structureWallMs,
                    SearchMs = result.SearchMilliseconds,
                    RefineMs = result.RefineMilliseconds,
                    ReferencePreprocessMs = refPrepMs,
                    LivePreprocessMs = livePrepMs,
                    DistanceMapMs = distMapMs,
                    ReferenceDiskMs = referenceLoadMs,
                    ReferenceCacheHit = cacheHit,
                    DownscaleFactor = downscaleFactor,
                    TopCandidates = result.Candidates.Select(c => new CandidateDetail
                    {
                        Scale = c.Scale,
                        OffsetX = c.OffsetX,
                        OffsetY = c.OffsetY,
                        ChamferPixels = c.ChamferPixels,
                        EdgeCoverage = c.EdgeCoverage,
                        InlierCount = c.FeatureInlierCount,
                        RawScore = (int)Math.Round(c.CompositeCost),
                        FinalScore = c.CompositeCost,
                        IsWithinValidBounds = c.IsWithinValidBounds
                    }).ToArray()
                };

                if (result.Accepted && finalResult is null)
                {
                    finalResult = new ProbeResult
                    {
                        Strategy = StrategyName,
                        Command = "run",
                        Succeeded = true,
                        MapId = map.Id.ToString(),
                        MapDisplayName = map.DisplayName,
                        Confidence = result.Confidence,
                        Transform = new TransformInfo
                        {
                            ScaleX = result.Transform!.ScaleX / downscaleFactor,
                            ScaleY = result.Transform!.ScaleY / downscaleFactor,
                            OffsetX = result.Transform.OffsetX / downscaleFactor,
                            OffsetY = result.Transform.OffsetY / downscaleFactor,
                            ReferenceWidth = reference.Width,
                            ReferenceHeight = reference.Height,
                            AlignmentMode = "Uniform"
                        }
                    };
                }
            }
            else if (finalResult is null
                && MapOverlayTransformSolver.TrySolve(
                    candidate,
                    MapOverlayAlignmentMode.Uniform,
                    out var transform,
                    out _))
            {
                finalResult = new ProbeResult
                {
                    Strategy = StrategyName,
                    Command = "run",
                    Succeeded = true,
                    MapId = map.Id.ToString(),
                    MapDisplayName = map.DisplayName,
                    Confidence = candidate.Score,
                    Transform = new TransformInfo
                    {
                        ScaleX = transform.ScaleX,
                        ScaleY = transform.ScaleY,
                        OffsetX = transform.OffsetX,
                        OffsetY = transform.OffsetY,
                        ReferenceWidth = candidate.Fingerprint.ReferenceWidth,
                        ReferenceHeight = candidate.Fingerprint.ReferenceHeight,
                        AlignmentMode = "Uniform"
                    }
                };
            }

            candidates.Add(new CandidateInfo
            {
                MapId = map.Id.ToString(),
                MapDisplayName = map.DisplayName,
                FloorKey = "First",
                VectorError = candidate.VectorError,
                Score = candidate.Score,
                EstimatedScaleX = candidate.EstimatedScaleX,
                EstimatedScaleY = candidate.EstimatedScaleY,
                MainGate = new GateInfo
                {
                    Score = candidate.MainGate.Score,
                    Scale = candidate.MainGate.Scale,
                    Bounds = ToBoundsInfo(candidate.MainGate.ScreenBounds)
                },
                SideGate = new GateInfo
                {
                    Score = candidate.SideGate.Score,
                    Scale = candidate.SideGate.Scale,
                    Bounds = ToBoundsInfo(candidate.SideGate.ScreenBounds)
                },
                Structure = structureInfo,
                TransformSource = structureInfo?.Accepted == true ? "structure" : "geometry"
            });
        }

        totalTimer.Stop();

        // 构建最终结果（所有 init-only 属性在构造时一次性赋值）
        var resultDoc = finalResult is not null
            ? finalResult with
            {
                Phases = new PhaseTimings
                {
                    LoadMs = loadMs,
                    GateCreateMatchImageMs = gateCreateMatchMs,
                    GateDetectMs = gateDetectMs,
                    CatalogLoadMs = catalogMs,
                    FingerprintBuildMs = fingerprintMs,
                    GeometryRankMs = geometryMs,
                    ReferenceLoadMs = referenceLoadMs,
                    StructureWallMs = structureWallMs,
                    TotalWallMs = totalTimer.Elapsed.TotalMilliseconds
                },
                Candidates = candidates,
                ImageWidth = fullImage.Width,
                ImageHeight = fullImage.Height
            }
            : new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = false,
                FailureReason = candidates.Count == 0 ? "无候选地图匹配" : "无候选通过门槛",
                Confidence = 0d,
                Phases = new PhaseTimings
                {
                    LoadMs = loadMs,
                    GateCreateMatchImageMs = gateCreateMatchMs,
                    GateDetectMs = gateDetectMs,
                    CatalogLoadMs = catalogMs,
                    FingerprintBuildMs = fingerprintMs,
                    GeometryRankMs = geometryMs,
                    ReferenceLoadMs = referenceLoadMs,
                    StructureWallMs = structureWallMs,
                    TotalWallMs = totalTimer.Elapsed.TotalMilliseconds
                },
                Candidates = candidates,
                ImageWidth = fullImage.Width,
                ImageHeight = fullImage.Height
            };

        // 输出 JSON
        JsonOutputWriter.WriteLine(resultDoc);
        if (context.OutputPath is not null)
            await JsonOutputWriter.WriteAsync(resultDoc, context.OutputPath);

        return resultDoc;
    }

    private static ProbeResult Fail(string reason, Stopwatch timer)
    {
        timer.Stop();
        return new ProbeResult
        {
            Strategy = "dual-gate",
            Command = "run",
            Succeeded = false,
            FailureReason = reason,
            Phases = new PhaseTimings { TotalWallMs = timer.Elapsed.TotalMilliseconds }
        };
    }

    private static MapGeometryFingerprint? BuildFingerprint(MapRecord map)
    {
        var profile = map.Recognition.FirstFloor;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
        if (main?.Bounds?.IsValid is not true
            || side?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        var pixelWidth = profile.RecognitionPixelWidth;
        var pixelHeight = profile.RecognitionPixelHeight;
        return new MapGeometryFingerprint
        {
            Map = map,
            MainPoint = new MapNormalizedPoint(
                main.Bounds.X + main.Bounds.Width / 2d,
                main.Bounds.Y + main.Bounds.Height / 2d),
            SidePoint = new MapNormalizedPoint(
                side.Bounds.X + side.Bounds.Width / 2d,
                side.Bounds.Y + side.Bounds.Height / 2d),
            MainReferenceBounds = new MapScreenRect(
                main.Bounds.X * pixelWidth,
                main.Bounds.Y * pixelHeight,
                main.Bounds.Width * pixelWidth,
                main.Bounds.Height * pixelHeight),
            SideReferenceBounds = new MapScreenRect(
                side.Bounds.X * pixelWidth,
                side.Bounds.Y * pixelHeight,
                side.Bounds.Width * pixelWidth,
                side.Bounds.Height * pixelHeight),
            ReferenceWidth = pixelWidth,
            ReferenceHeight = pixelHeight
        };
    }

    private static Mat DownscaleImage(Mat source, double factor, out double _)
    {
        if (factor <= 0d || factor >= 1d)
        {
            _ = 0d;
            return source.Clone();
        }
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));
        var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(width, height), interpolation: InterpolationFlags.Area);
        _ = 0d;
        return scaled;
    }

    private static double EffectiveDownscaleFactor(double factor) =>
        double.IsFinite(factor) && factor > 0d && factor < 1d ? factor : 1d;

    private static GateBoundsInfo? ToBoundsInfo(MapScreenRect bounds) =>
        bounds.IsValid
            ? new GateBoundsInfo { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height }
            : null;
}
