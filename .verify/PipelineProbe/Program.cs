using System.Diagnostics;
using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

return await PipelineProbe.RunAsync(args);

internal static class PipelineProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }
        try
        {
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            return command switch
            {
                "run" => await RunRegistrationAsync(options),
                "masks" => await RunMasksAsync(options),
                "visible-match" => await RunVisibleMatchAsync(options),
                "calibrate" => await CalibrateAsync(options),
                _ => throw new ArgumentException($"未知命令：{args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // run — 对单张截图运行 MapStructureRegistrar.Register()
    // ═══════════════════════════════════════════════════════════════

    private static async Task<int> RunRegistrationAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Required(options, "image");
        var mapId = Required(options, "map");
        var floor = options.TryGetValue("floor", out var f)
            && f.Equals("2", StringComparison.OrdinalIgnoreCase)
                ? MapFloor.Second
                : MapFloor.First;

        // 加载地图目录
        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        var map = maps.FirstOrDefault(m => m.Id == Guid.Parse(mapId))
            ?? throw new ArgumentException($"未找到地图：{mapId}");

        var referencePath = floor == MapFloor.First
            ? repository.GetFloorOnePath(map)
            : repository.GetFloorTwoPath(map);
        if (!File.Exists(referencePath))
            throw new FileNotFoundException("找不到参考地图。", referencePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("找不到截图。", imagePath);

        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        using var live = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (reference.Empty())
            throw new InvalidOperationException("参考地图为空。");
        if (live.Empty())
            throw new InvalidOperationException("截图为空。");

        // 可选下采样
        var downscaleFactor = Double(options, "downscale", 0.5d);
        using var liveForProcess = DownscaleImage(live, downscaleFactor);
        var imageScale = downscaleFactor > 0d ? 1d / downscaleFactor : 1d;

        // 缩放参数
        var scale = Double(options, "scale", 1d);
        var lockedScale = scale * imageScale;
        var offsetX = Double(options, "offset-x", 0d);
        var offsetY = Double(options, "offset-y", 0d);
        var enableEcc = !Flag(options, "no-ecc");
        var enableFeatureVoting = !Flag(options, "no-feature");
        var allowScaleSearch = Flag(options, "allow-scale-search");
        var restrictSearch = Flag(options, "restrict-search");

        // 调优（需在预处理之前，因为 EnableVisibleMask 控制 VisibleMask 生成）
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            EnableDebugOutput = !Flag(options, "no-debug"),
            EnableEccRefinement = enableEcc,
            EnableFeatureVoting = enableFeatureVoting,
            ReusePreviousAlignmentResult = false,
            PreviousAlignmentSearchRadiusPixels = (int)Double(options, "reuse-radius", 96d),
            TopCandidateCount = (int)Double(options, "top-candidates", 6d),
            StructureFallbackBudgetMilliseconds = (int)Double(options, "time-budget", 1500),
            // Visible-aware switches
            EnableVisibleMask = Flag(options, "visible"),
            EnableVisibleAwareShadow = Flag(options, "visible-shadow"),
            EnableVisibleAwareInjection = Flag(options, "visible-inject"),
            EnableVisibleAwareEarlyExit = Flag(options, "visible-early-exit"),
            VisibleAwareEarlyTerminationMaxCompositeCost =
                Double(options, "early-exit-threshold", 0d),
            VisibleAwareCoarseDownsample = (int)Double(options, "va-downsample", 4),
            VisibleAwareTopK = (int)Double(options, "va-topk", 5)
        };

        // 预处理
        var preprocessor = new MapStructurePreprocessor();
        var preprocessTimer = Stopwatch.StartNew();
        using var preparedReference = preprocessor.ProcessCachedReference(
            reference, referencePath, out var refTiming, out var cacheHit);
        using var preparedLive = preprocessor.ProcessLiveRoi(
            liveForProcess, null, null,
            generateVisibleMask: tuning.EnableVisibleMask);
        preparedReference.GetOrCreateReferenceDistanceMap();
        var distMapTimer = Stopwatch.StartNew();
        preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
        distMapTimer.Stop();
        preprocessTimer.Stop();

        var debugDir = tuning.EnableDebugOutput
            ? Path.GetFullPath(Get(options, "debug-dir",
                Path.Combine(Environment.CurrentDirectory, "pipeline-debug",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))))
            : null;

        // 构造请求
        var viewport = new MapScreenRect(0d, 0d, liveForProcess.Width, liveForProcess.Height);
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = liveForProcess,
            ViewportBounds = viewport,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = lockedScale,
                ScaleY = lockedScale,
                OffsetX = offsetX * downscaleFactor,
                OffsetY = offsetY * downscaleFactor,
                ReferenceWidth = reference.Width,
                ReferenceHeight = reference.Height,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            AllowScaleSearch = allowScaleSearch,
            RestrictSearchToLockedTransform = restrictSearch,
            TrackingMode = restrictSearch,
            ForceBestCandidate = Flag(options, "force-best"),
            DebugOutputDirectory = debugDir,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive,
            ValidMapBounds = map.Recognition.GetFloor(floor).GetEffectiveValidMapBounds()
        };

        // 执行
        var registrar = new MapStructureRegistrar(preprocessor);
        var registerTimer = Stopwatch.StartNew();
        var result = registrar.Register(request);
        registerTimer.Stop();

        // 输出结果
        var document = new
        {
            Accepted = result.Accepted,
            Confidence = result.Confidence,
            Transform = result.Transform is { } t ? new
            {
                t.ScaleX, t.ScaleY,
                t.OffsetX, t.OffsetY,
                t.ReferenceWidth, t.ReferenceHeight,
                t.AlignmentMode
            } : null,
            RejectionReason = result.RejectionReason.ToString(),
            result.FailureReason,
            result.BestScore,
            result.SecondScore,
            result.CandidateMargin,
            TopCandidates = result.Candidates
                .OrderBy(c => c.CompositeCost)
                .Take(6)
                .Select(c => new
                {
                    c.Scale,
                    c.ReferenceX, c.ReferenceY,
                    c.OffsetX, c.OffsetY,
                    c.CompositeCost,
                    c.ChamferPixels,
                    c.EdgeCoverage,
                    c.OccupancyCoverage,
                    c.ConsistentPartitions,
                    c.UsedGlobalSearch,
                    c.FeatureInlierCount,
                    c.FeatureConsensus,
                    c.PriorAgreement,
                    c.IsWithinValidBounds,
                    c.EccConverged,
                    c.EccCorrelation,
                })
                .ToList(),
            VisibleAware = new
            {
                VisibleMaskMs = result.VisibleMaskMilliseconds,
                result.VisibleFraction,
                result.VisibleStructurePixels,
                result.VisibleEdgePixels,
                SearchMs = result.VisibleAwareSearchMilliseconds,
                CandidateCount = result.VisibleAwareCandidateCount,
                TopCost = result.VisibleAwareTopCost,
                TopMargin = result.VisibleAwareTopMargin,
                EarlyAccepted = result.VisibleAwareEarlyAccepted,
                FallbackReason = result.VisibleAwareFallbackReason
            },
            Timings = new
            {
                PreprocessMs = preprocessTimer.Elapsed.TotalMilliseconds,
                PreprocessRefMs = refTiming.TotalMs,
                PreprocessRefCacheHit = cacheHit,
                DistanceMapMs = distMapTimer.Elapsed.TotalMilliseconds,
                RegisterSearchMs = result.SearchMilliseconds,
                RegisterRefineMs = result.RefineMilliseconds,
                RegisterTotalMs = registerTimer.Elapsed.TotalMilliseconds,
                QueryEdgePixels = result.QueryEdgePixels,
                QueryBounds = new { result.QueryBoundsX, result.QueryBoundsY, result.QueryBoundsWidth, result.QueryBoundsHeight },
                ScaleHypothesisCount = result.ScaleHypothesisCount,
                OversizedHypothesisCount = result.OversizedHypothesisCount,
                UsedRestrictedSearch = result.UsedRestrictedSearch,
                WasForcedBestCandidate = result.WasForcedBestCandidate,
                FeatureMatchCount = result.FeatureMatchCount,
                FeatureInlierCount = result.FeatureInlierCount,
                FeatureConsensus = result.FeatureConsensus,
                EccConverged = result.EccConverged,
                EccCorrelation = result.EccCorrelation
            },
            Input = new
            {
                Image = imagePath,
                MapId = map.Id.ToString(),
                MapName = map.DisplayName,
                Floor = floor.ToString(),
                ReferencePath = referencePath,
                ReferenceSize = new { reference.Width, reference.Height },
                LiveSize = new { live.Width, live.Height },
                DownscaleFactor = downscaleFactor,
                LockedScale = lockedScale,
                AllowScaleSearch = allowScaleSearch,
                RestrictSearch = restrictSearch
            }
        };

        var output = Get(options, "out",
            Path.Combine(debugDir ?? Environment.CurrentDirectory, "result.json"));
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);
        File.WriteAllText(output, JsonSerializer.Serialize(document, JsonOptions));

        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return result.Accepted ? 0 : 1;
    }

    // ═══════════════════════════════════════════════════════════════
    // masks — 输出结构预处理诊断图片（Phase 1）
    // ═══════════════════════════════════════════════════════════════

    private static async Task<int> RunMasksAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Required(options, "image");
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("找不到截图。", imagePath);

        var outputDir = Path.GetFullPath(Get(options, "out",
            Path.Combine(Environment.CurrentDirectory, "mask-debug",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))));
        Directory.CreateDirectory(outputDir);

        using var live = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (live.Empty())
            throw new InvalidOperationException("截图为空。");

        // 设置开关：启用 VisibleMask
        var tuning = new MapStructureRegistrationTuning
        {
            EnableVisibleMask = true,
            EnableDebugOutput = true
        };

        var preprocessor = new MapStructurePreprocessor();
        var preprocessTimer = Stopwatch.StartNew();
        using var features = preprocessor.ProcessLiveRoi(
            live, null, null, generateVisibleMask: true);
        preprocessTimer.Stop();

        // 输出诊断图片
        Cv2.ImWrite(Path.Combine(outputDir, "live.png"), live);
        Cv2.ImWrite(Path.Combine(outputDir, "nuisance.png"), features.NuisanceMask);
        Cv2.ImWrite(Path.Combine(outputDir, "structure-original.png"), features.StructureMask);
        Cv2.ImWrite(Path.Combine(outputDir, "edges-original.png"), features.Edges);

        if (features.RawVisibleMask is not null && !features.RawVisibleMask.Empty())
        {
            Cv2.ImWrite(Path.Combine(outputDir, "raw-visible.png"), features.RawVisibleMask);

            using var safeVisible = features.CreateSafeVisibleMask(
                tuning.SafeVisibleMaskErodePixels);
            if (safeVisible is not null && !safeVisible.Empty())
            {
                Cv2.ImWrite(Path.Combine(outputDir, "safe-visible.png"), safeVisible);

                using var visibleStructure = new Mat();
                Cv2.BitwiseAnd(features.StructureMask, safeVisible, visibleStructure);
                Cv2.ImWrite(Path.Combine(outputDir, "structure-visible.png"), visibleStructure);

                using var visibleEdges = new Mat();
                Cv2.BitwiseAnd(features.Edges, safeVisible, visibleEdges);
                Cv2.ImWrite(Path.Combine(outputDir, "edges-visible.png"), visibleEdges);
            }
        }

        var document = new
        {
            Image = imagePath,
            OutputDirectory = outputDir,
            PreprocessMs = preprocessTimer.Elapsed.TotalMilliseconds,
            Features = new
            {
                StructurePixels = Cv2.CountNonZero(features.StructureMask),
                EdgePixels = Cv2.CountNonZero(features.Edges),
                NuisancePixels = Cv2.CountNonZero(features.NuisanceMask),
                RawVisiblePixels = features.RawVisibleMask is { } rvm && !rvm.Empty()
                    ? Cv2.CountNonZero(rvm)
                    : 0,
                VisibleFraction = features.RawVisibleMask is { } rvm2 && !rvm2.Empty()
                    ? (double)Cv2.CountNonZero(rvm2) / (rvm2.Width * rvm2.Height)
                    : 0d,
                HasVisibleMask = features.RawVisibleMask is not null && !features.RawVisibleMask.Empty()
            }
        };

        var reportPath = Path.Combine(outputDir, "mask-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(document, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // visible-match — 对单张截图运行 Visible-aware 候选搜索（离线，不注入）
    // ═══════════════════════════════════════════════════════════════

    private static async Task<int> RunVisibleMatchAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Required(options, "image");
        var mapId = Required(options, "map");
        var floor = options.TryGetValue("floor", out var f)
            && f.Equals("2", StringComparison.OrdinalIgnoreCase)
                ? MapFloor.Second
                : MapFloor.First;

        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        var map = maps.FirstOrDefault(m => m.Id == Guid.Parse(mapId))
            ?? throw new ArgumentException($"未找到地图：{mapId}");

        var referencePath = floor == MapFloor.First
            ? repository.GetFloorOnePath(map)
            : repository.GetFloorTwoPath(map);
        if (!File.Exists(referencePath))
            throw new FileNotFoundException("找不到参考地图。", referencePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("找不到截图。", imagePath);

        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        using var live = Cv2.ImRead(imagePath, ImreadModes.Unchanged);

        var downscaleFactor = Double(options, "downscale", 0.5d);
        using var liveForProcess = DownscaleImage(live, downscaleFactor);
        var imageScale = downscaleFactor > 0d ? 1d / downscaleFactor : 1d;
        var scale = Double(options, "scale", 1d);
        var lockedScale = scale * imageScale;

        var preprocessor = new MapStructurePreprocessor();
        var preprocessTimer = Stopwatch.StartNew();
        using var preparedReference = preprocessor.ProcessCachedReference(
            reference, referencePath, out var refTiming, out var cacheHit);
        using var preparedLive = preprocessor.ProcessLiveRoi(
            liveForProcess, null, null,
            generateVisibleMask: true);  // 启用 VisibleMask
        preparedReference.GetOrCreateReferenceDistanceMap();
        preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
        preprocessTimer.Stop();

        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            EnableDebugOutput = !Flag(options, "no-debug"),
            EnableVisibleMask = true,
            EnableVisibleAwareShadow = true,     // 搜索但不注入
            EnableVisibleAwareInjection = Flag(options, "inject"),
            EnableFeatureVoting = !Flag(options, "no-feature"),
            EnableEccRefinement = !Flag(options, "no-ecc"),
            TopCandidateCount = (int)Double(options, "top-candidates", 6d),
            StructureFallbackBudgetMilliseconds =
                (int)Double(options, "time-budget", 1500)
        };

        var debugDir = tuning.EnableDebugOutput
            ? Path.GetFullPath(Get(options, "debug-dir",
                Path.Combine(Environment.CurrentDirectory, "visible-match-debug",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))))
            : null;

        var viewport = new MapScreenRect(0d, 0d, liveForProcess.Width, liveForProcess.Height);
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = liveForProcess,
            ViewportBounds = viewport,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = lockedScale, ScaleY = lockedScale,
                ReferenceWidth = reference.Width, ReferenceHeight = reference.Height,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            AllowScaleSearch = Flag(options, "allow-scale-search"),
            RestrictSearchToLockedTransform = Flag(options, "restrict-search"),
            DebugOutputDirectory = debugDir,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive,
            ValidMapBounds = map.Recognition.GetFloor(floor).GetEffectiveValidMapBounds()
        };

        var registrar = new MapStructureRegistrar(preprocessor);
        var registerTimer = Stopwatch.StartNew();
        var result = registrar.Register(request);
        registerTimer.Stop();

        var document = new
        {
            Accepted = result.Accepted,
            VisibleAware = new
            {
                VisibleFraction = result.VisibleFraction,
                VisibleStructurePixels = result.VisibleStructurePixels,
                VisibleEdgePixels = result.VisibleEdgePixels,
                SearchMs = result.VisibleAwareSearchMilliseconds,
                CandidateCount = result.VisibleAwareCandidateCount,
                TopCost = result.VisibleAwareTopCost,
                TopMargin = result.VisibleAwareTopMargin,
                EarlyAccepted = result.VisibleAwareEarlyAccepted,
                FallbackReason = result.VisibleAwareFallbackReason
            },
            TopCandidates = result.Candidates
                .OrderBy(c => c.CompositeCost)
                .Take(10)
                .Select(c => new
                {
                    c.Scale, c.ReferenceX, c.ReferenceY,
                    c.CompositeCost, c.ChamferPixels, c.EdgeCoverage,
                    c.OccupancyCoverage, c.ConsistentPartitions,
                    c.FromVisibleAware, c.VisibleFraction,
                    c.VisibleStructurePixels, c.VisibleEdgePixels,
                    c.UsedGlobalSearch, c.FeatureConsensus
                })
                .ToList(),
            Timings = new
            {
                PreprocessMs = preprocessTimer.Elapsed.TotalMilliseconds,
                RegisterTotalMs = registerTimer.Elapsed.TotalMilliseconds,
                result.SearchMilliseconds,
                result.RefineMilliseconds,
                VisibleMaskMs = result.VisibleMaskMilliseconds
            },
            Input = new { imagePath, mapId, floor = floor.ToString(), lockedScale }
        };

        var output = Get(options, "out",
            Path.Combine(debugDir ?? Environment.CurrentDirectory, "visible-match-result.json"));
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);
        File.WriteAllText(output, JsonSerializer.Serialize(document, JsonOptions));

        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // calibrate — 批量收集 Shadow 数据用于阈值调优（Phase 5）
    // ═══════════════════════════════════════════════════════════════

    private static async Task<int> CalibrateAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Required(options, "image");
        var mapId = Required(options, "map");
        var floor = options.TryGetValue("floor", out var f)
            && f.Equals("2", StringComparison.OrdinalIgnoreCase)
                ? MapFloor.Second
                : MapFloor.First;

        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        var map = maps.FirstOrDefault(m => m.Id == Guid.Parse(mapId))
            ?? throw new ArgumentException($"未找到地图：{mapId}");

        var referencePath = floor == MapFloor.First
            ? repository.GetFloorOnePath(map)
            : repository.GetFloorTwoPath(map);
        if (!File.Exists(referencePath))
            throw new FileNotFoundException("找不到参考地图。", referencePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("找不到截图。", imagePath);

        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        using var live = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (reference.Empty() || live.Empty())
            throw new InvalidOperationException("图像为空。");

        var downscaleFactor = Double(options, "downscale", 0.5d);
        using var liveForProcess = DownscaleImage(live, downscaleFactor);
        var imageScale = downscaleFactor > 0d ? 1d / downscaleFactor : 1d;
        var scale = Double(options, "scale", 1d);
        var lockedScale = scale * imageScale;

        var preprocessor = new MapStructurePreprocessor();
        var preprocessTimer = Stopwatch.StartNew();
        using var preparedReference = preprocessor.ProcessCachedReference(
            reference, referencePath, out var refTiming, out var cacheHit);
        using var preparedLive = preprocessor.ProcessLiveRoi(
            liveForProcess, null, null,
            generateVisibleMask: true);  // 强制生成 VisibleMask
        preparedReference.GetOrCreateReferenceDistanceMap();
        preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
        preprocessTimer.Stop();

        var testThreshold = Double(options, "threshold", 2.0d);

        // 启用全部 Visible-aware 开关 + 提前终止
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            EnableDebugOutput = !Flag(options, "no-debug"),
            EnableEccRefinement = !Flag(options, "no-ecc"),
            EnableFeatureVoting = !Flag(options, "no-feature"),
            ReusePreviousAlignmentResult = false,
            TopCandidateCount = (int)Double(options, "top-candidates", 6d),
            StructureFallbackBudgetMilliseconds =
                (int)Double(options, "time-budget", 1500),
            // 全量启用 Visible-aware
            EnableVisibleMask = true,
            EnableVisibleAwareShadow = true,
            EnableVisibleAwareInjection = true,
            EnableVisibleAwareEarlyExit = true,
            VisibleAwareEarlyTerminationMaxCompositeCost = testThreshold,
            VisibleAwareCoarseDownsample = (int)Double(options, "va-downsample", 4),
            VisibleAwareTopK = (int)Double(options, "va-topk", 5)
        };

        var debugDir = tuning.EnableDebugOutput
            ? Path.GetFullPath(Get(options, "debug-dir",
                Path.Combine(Environment.CurrentDirectory, "calibrate-debug",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))))
            : null;

        var viewport = new MapScreenRect(0d, 0d, liveForProcess.Width, liveForProcess.Height);
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = liveForProcess,
            ViewportBounds = viewport,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = lockedScale, ScaleY = lockedScale,
                OffsetX = 0d, OffsetY = 0d,
                ReferenceWidth = reference.Width, ReferenceHeight = reference.Height,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            AllowScaleSearch = Flag(options, "allow-scale-search"),
            RestrictSearchToLockedTransform = false,
            DebugOutputDirectory = debugDir,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive,
            ValidMapBounds = map.Recognition.GetFloor(floor).GetEffectiveValidMapBounds()
        };

        var registrar = new MapStructureRegistrar(preprocessor);
        var registerTimer = Stopwatch.StartNew();
        var result = registrar.Register(request);
        registerTimer.Stop();

        // 提取顶级候选的详细评分（供阈值分析）
        var topVisibleAware = result.Candidates
            .Where(c => c.FromVisibleAware)
            .OrderBy(c => c.CompositeCost)
            .Take(3)
            .Select(c => new
            {
                c.CompositeCost, c.ChamferPixels, c.EdgeCoverage,
                c.OccupancyCoverage, c.ConsistentPartitions,
                c.PriorAgreement, c.IsWithinValidBounds,
                c.VisibleFraction, c.VisibleStructurePixels, c.VisibleEdgePixels
            })
            .ToList();

        var topLegacy = result.Candidates
            .Where(c => !c.FromVisibleAware)
            .OrderBy(c => c.CompositeCost)
            .Take(3)
            .Select(c => new
            {
                c.CompositeCost, c.ChamferPixels, c.EdgeCoverage,
                c.OccupancyCoverage, c.ConsistentPartitions,
                c.PriorAgreement, c.UsedGlobalSearch
            })
            .ToList();

        var document = new
        {
            Image = imagePath,
            MapId = mapId,
            Floor = floor.ToString(),
            TestThreshold = testThreshold,
            Result = new
            {
                Accepted = result.Accepted,
                Confidence = result.Confidence,
                RejectionReason = result.RejectionReason.ToString(),
                result.FailureReason,
                result.BestScore,
                result.SecondScore,
                result.CandidateMargin
            },
            VisibleAware = new
            {
                VisibleMaskMs = result.VisibleMaskMilliseconds,
                result.VisibleFraction,
                result.VisibleStructurePixels,
                result.VisibleEdgePixels,
                SearchMs = result.VisibleAwareSearchMilliseconds,
                CandidateCount = result.VisibleAwareCandidateCount,
                TopCost = result.VisibleAwareTopCost,
                TopMargin = result.VisibleAwareTopMargin,
                EarlyAccepted = result.VisibleAwareEarlyAccepted,
                FallbackReason = result.VisibleAwareFallbackReason
            },
            TopVisibleAwareCandidates = topVisibleAware,
            TopLegacyCandidates = topLegacy,
            Timings = new
            {
                PreprocessMs = preprocessTimer.Elapsed.TotalMilliseconds,
                RefPreprocessMs = refTiming.TotalMs,
                RefCacheHit = cacheHit,
                RegisterSearchMs = result.SearchMilliseconds,
                RegisterRefineMs = result.RefineMilliseconds,
                RegisterTotalMs = registerTimer.Elapsed.TotalMilliseconds
            }
        };

        var output = Get(options, "out",
            Path.Combine(debugDir ?? Environment.CurrentDirectory, "calibrate-result.json"));
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);
        File.WriteAllText(output, JsonSerializer.Serialize(document, JsonOptions));

        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════════════════════════

    private static Mat DownscaleImage(Mat source, double factor)
    {
        if (factor <= 0d || Math.Abs(factor - 1d) < 0.001d)
            return source.Clone();
        var target = new Size(
            Math.Max(1, (int)Math.Round(source.Width * factor)),
            Math.Max(1, (int)Math.Round(source.Height * factor)));
        var result = new Mat();
        Cv2.Resize(source, result, target, 0d, 0d, InterpolationFlags.Area);
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key)
    {
        if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new ArgumentException($"缺少必需参数：--{key}");
    }

    private static string Get(IReadOnlyDictionary<string, string> options, string key, string fallback)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : fallback;

    private static double Double(IReadOnlyDictionary<string, string> options, string key, double fallback)
        => options.TryGetValue(key, out var value)
           && double.TryParse(value, out var result)
           && double.IsFinite(result)
            ? result : fallback;

    private static bool Flag(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out _);

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    result[key] = args[++i];
                else
                    result[key] = string.Empty; // flag
            }
            else if (arg.StartsWith("-") && arg.Length > 1 && !char.IsDigit(arg[1]))
            {
                var key = arg[1..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    result[key] = args[++i];
                else
                    result[key] = string.Empty;
            }
        }
        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PipelineProbe — 结构配准管线验证工具");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  run           对单张截图运行 MapStructureRegistrar.Register()");
        Console.WriteLine("  masks         输出结构预处理诊断图片");
        Console.WriteLine("  visible-match 仅运行 Visible-aware 候选搜索（离线，不注入）");
        Console.WriteLine("  calibrate     批量收集 Shadow 数据用于阈值调优");
        Console.WriteLine();
        Console.WriteLine("run 必需参数：");
        Console.WriteLine("  --image <path>     游戏截图 PNG");
        Console.WriteLine("  --map <guid>        地图 ID（来自 maps.json）");
        Console.WriteLine();
        Console.WriteLine("run 可选参数：");
        Console.WriteLine("  --floor 1|2         楼层（默认 1）");
        Console.WriteLine("  --scale <double>    锁定缩放（默认 1）");
        Console.WriteLine("  --offset-x <double> 初始偏移 X（默认 0）");
        Console.WriteLine("  --offset-y <double> 初始偏移 Y（默认 0）");
        Console.WriteLine("  --downscale <factor> 下采样倍率（默认 0.5）");
        Console.WriteLine("  --allow-scale-search 允许缩放搜索");
        Console.WriteLine("  --restrict-search    限制搜索到锁定变换附近");
        Console.WriteLine("  --no-ecc             禁用 ECC 精修");
        Console.WriteLine("  --no-feature         禁用 ORB 特征投票");
        Console.WriteLine("  --no-debug           禁用调试输出");
        Console.WriteLine("  --force-best         强制返回最佳候选");
        Console.WriteLine("  --time-budget <ms>   结构搜索时间预算（默认 1500）");
        Console.WriteLine("  --top-candidates <n> Top-K 候选数（默认 6）");
        Console.WriteLine("  --out <path>         JSON 输出路径");
        Console.WriteLine();
        Console.WriteLine("  Visible-aware 开关（Phase 5）：");
        Console.WriteLine("  --visible            启用 VisibleMask 生成");
        Console.WriteLine("  --visible-shadow     启用 Shadow 模式（搜索但不注入）");
        Console.WriteLine("  --visible-inject     将 Visible-aware 候选注入候选列表");
        Console.WriteLine("  --visible-early-exit 启用提前终止");
        Console.WriteLine("  --early-exit-threshold <n>  提前终止 CompositeCost 阈值");
        Console.WriteLine("  --va-downsample <n>  Visible-aware 降采样（默认 4）");
        Console.WriteLine("  --va-topk <n>        Visible-aware Top-K（默认 5）");
        Console.WriteLine();
        Console.WriteLine("masks 参数：");
        Console.WriteLine("  --image <path>  游戏截图 PNG");
        Console.WriteLine("  --out <dir>      输出目录");
        Console.WriteLine();
        Console.WriteLine("calibrate 参数（等同 run + 自动启用全部 visible-aware 开关）：");
        Console.WriteLine("  --image <path>  游戏截图 PNG");
        Console.WriteLine("  --map <guid>     地图 ID");
        Console.WriteLine("  --out <path>     JSON 输出路径");
        Console.WriteLine("  --threshold <n>  测试的提前终止阈值（默认 2.0）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  PipelineProbe.exe run --image zero-gate.png --map abc-123 --scale 0.85");
        Console.WriteLine("  PipelineProbe.exe masks --image zero-gate.png --out ./debug");
        Console.WriteLine("  PipelineProbe.exe run --image zero-gate.png --map abc-123 --visible --visible-inject");
        Console.WriteLine("  PipelineProbe.exe run --image zero-gate.png --map abc-123 --visible --visible-early-exit --early-exit-threshold 2.5");
        Console.WriteLine("  PipelineProbe.exe calibrate --image zero-gate.png --map abc-123");
    }
}
