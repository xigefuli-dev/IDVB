using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Text.Json;

DpiAwareness.EnablePerMonitorV2();
return await ProbeProgram.RunAsync(args);

internal static class DpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    public static void EnablePerMonitorV2() =>
        _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}

internal static class ProbeProgram
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
                "capture" => await CaptureAsync(options),
                "floor" => await FloorAsync(options),
                "floor-image" => FloorImage(options),
                "gates-image" => GatesImage(options),
                "side-scan" => await SideScanAsync(options),
                "match" => Match(options),
                "label" => Label(options),
                "batch" => Batch(options),
                "confidence-replay" => await ConfidenceReplayAsync(options),
                "run" => await RunAsync(options),
                "stats" => await StatsAsync(options),
                _ => throw new ArgumentException($"未知命令：{args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> SideScanAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Required(options, "image");
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
            throw new InvalidOperationException("Unable to read side-scan image.");

        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        var templates = new List<(MapRecord map, string floorKey, Mat featureTemplate)>();
        try
        {
            foreach (var map in maps)
            foreach (var floor in MapFloorRules.GetOrderedFloors(map))
            {
                var profile = MapFloorRules.GetFloorProfile(map, floor.Key);
                if (profile is null
                    || string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
                {
                    continue;
                }
                var path = repository.GetSideEntranceFeaturePath(map, floor.Key);
                if (!File.Exists(path))
                    continue;
                var template = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (template.Empty())
                {
                    template.Dispose();
                    continue;
                }
                templates.Add((map, floor.Key, template));
            }

            var candidates = new SideEntranceScanPipeline().RunScan(
                image,
                templates,
                (int)Double(options, "top", 10d));
            Console.WriteLine(JsonSerializer.Serialize(
                candidates.Select(candidate => new
                {
                    candidate.Map.SequenceNumber,
                    candidate.Map.Id,
                    candidate.FloorKey,
                    candidate.MatchScore,
                    candidate.MatchScale,
                    candidate.MatchLocation
                }),
                JsonOptions));
            return candidates.Count > 0 ? 0 : 1;
        }
        finally
        {
            foreach (var (_, _, template) in templates)
                template.Dispose();
        }
    }

    private static async Task<int> CaptureAsync(IReadOnlyDictionary<string, string> options)
    {
        var settingsPath = Get(
            options,
            "settings",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IDVBuff",
                "MapRuntime",
                "settings.json"));
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("找不到运行设置。", settingsPath);
        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
            await File.ReadAllTextAsync(settingsPath),
            JsonOptions) ?? new MapRuntimeSettings();
        settings.Normalize();
        if (!settings.IsMapViewportCalibrated || settings.MapViewportRegion is null)
            throw new InvalidOperationException("运行设置中没有有效的地图显示边界校准。");

        var delay = Int(options, "delay", 3);
        Console.WriteLine($"{delay} 秒后捕获；请切回游戏并打开地图。");
        await Task.Delay(TimeSpan.FromSeconds(delay));
        var capture = new DwrGameWindowCaptureService();
        if (!capture.TryCaptureViewport(
                settings.MapViewportRegion,
                out var frame,
                out var failure)
            || frame is null)
        {
            throw new InvalidOperationException(failure);
        }

        using (frame)
        {
            var directory = Path.GetFullPath(Get(
                options,
                "out",
                Path.Combine(
                    Environment.CurrentDirectory,
                    "alignment-samples",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            Directory.CreateDirectory(directory);
            var screenshotPath = Path.Combine(directory, "roi.png");
            Cv2.ImWrite(screenshotPath, frame.Image);
            var referencePath = ResolveSelectedReference(
                settings.SelectedMapId,
                options);
            var gatePath = Get(
                options,
                "gate",
                Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png"));
            IReadOnlyList<GateDetection> gates = [];
            if (File.Exists(gatePath))
            {
                using var detector = new GateTemplateDetector(gatePath);
                using var gray = GateTemplateDetector.CreateMatchImage(frame.Image);
                gates = detector.Detect(
                    gray,
                    frame.ViewportBounds,
                    frame.ClientBounds.Width,
                    settings.RecognitionTuning.GateTemplateThreshold);
            }
            var sample = new ProbeSample
            {
                MapId = settings.SelectedMapId,
                ReferencePath = referencePath,
                ScreenshotPath = screenshotPath,
                Viewport = frame.ViewportBounds,
                Client = frame.ClientBounds,
                Gates = gates.Select(gate => new ProbeGate
                {
                    Score = gate.Score,
                    Scale = gate.Scale,
                    Bounds = gate.ScreenBounds
                }).ToArray()
            };
            if (TrySolveGateTruth(
                    settings.SelectedMapId,
                    gates,
                    frame.ViewportBounds,
                    out var gateTruth))
            {
                sample.History = gateTruth;
                sample.Truth = gateTruth;
            }
            else if (options.TryGetValue("history", out var historyPath)
                && File.Exists(historyPath))
            {
                var historySample = JsonSerializer.Deserialize<ProbeSample>(
                    File.ReadAllText(historyPath),
                    JsonOptions);
                sample.History = historySample?.Truth ?? historySample?.History;
            }
            await File.WriteAllTextAsync(
                Path.Combine(directory, "sample.json"),
                JsonSerializer.Serialize(sample, JsonOptions));
            Console.WriteLine($"已保存：{directory}");
            Console.WriteLine(
                sample.Truth is null
                    ? $"门候选：{gates.Count}；可用 label 命令补充真值。"
                    : $"门候选：{gates.Count}；已用双门生成初始真值，可继续用 label 检查。");
            return 0;
        }
    }

    private static async Task<int> FloorAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var settingsPath = Get(
            options,
            "settings",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IDVBuff",
                "MapRuntime",
                "settings.json"));
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("找不到运行设置。", settingsPath);
        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
            await File.ReadAllTextAsync(settingsPath),
            JsonOptions) ?? new MapRuntimeSettings();
        settings.Normalize();
        if (!settings.IsFloorDisplayCalibrated
            || settings.FloorDisplayRegion is null)
        {
            throw new InvalidOperationException("运行设置中没有有效的楼层显示区校准。");
        }

        var delay = Int(options, "delay", 3);
        var repeat = Math.Clamp(Int(options, "repeat", 100), 1, 10000);
        var outputPath = Get(options, "out", string.Empty);
        Console.WriteLine(
            $"{delay} 秒后连续识别 {repeat} 次；请切回游戏并保持地图打开。");
        await Task.Delay(TimeSpan.FromSeconds(delay));
        var firstPath = Get(
            options,
            "first",
            Path.Combine(AppContext.BaseDirectory, "Assets", "1F.png"));
        var secondPath = Get(
            options,
            "second",
            Path.Combine(AppContext.BaseDirectory, "Assets", "2F.png"));
        var recognizer = new FloorIndicatorRecognizer(firstPath, secondPath);
        using var capture = new FloorIndicatorCaptureService();
        var captureTotal = 0d;
        var analysisTotal = 0d;
        var endToEndTotal = 0d;
        var maximumEndToEnd = 0d;
        var firstCount = 0;
        var secondCount = 0;

        for (var index = 0; index < repeat; index++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (!capture.TryCapture(
                    settings.FloorDisplayRegion,
                    out var frame,
                    out var captureMilliseconds,
                    out var failure))
            {
                throw new InvalidOperationException(failure);
            }
            var result = recognizer.Recognize(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride);
            timer.Stop();
            if (index == 0)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    result,
                    JsonOptions));
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(fullOutputPath)!);
                    using var floorImage = Mat.FromPixelData(
                        frame.Height,
                        frame.Width,
                        MatType.CV_8UC4,
                        frame.Pixels,
                        frame.Stride);
                    Cv2.ImWrite(fullOutputPath, floorImage);
                }
            }
            if (!result.Succeeded || result.Floor is null)
            {
                throw new InvalidOperationException(
                    $"{result.FailureReason} "
                    + $"confidence={result.Confidence:F3}, "
                    + $"contrast={result.Contrast:F3}");
            }
            if (timer.Elapsed.TotalMilliseconds
                > MapFloorRecognitionRules.PerformanceBudgetMilliseconds)
            {
                throw new InvalidOperationException(
                    $"第 {index + 1} 次楼层识别耗时 "
                    + $"{timer.Elapsed.TotalMilliseconds:F1}ms，超过 100ms。");
            }
            if (result.Floor == "1f")
                firstCount++;
            else
                secondCount++;
            captureTotal += captureMilliseconds;
            analysisTotal += result.AnalysisMilliseconds;
            endToEndTotal += timer.Elapsed.TotalMilliseconds;
            maximumEndToEnd = Math.Max(
                maximumEndToEnd,
                timer.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"结果：1F {firstCount} 次，2F {secondCount} 次");
        Console.WriteLine(
            $"平均捕获 {captureTotal / repeat:F3}ms，"
            + $"平均判定 {analysisTotal / repeat:F3}ms，"
            + $"平均总计 {endToEndTotal / repeat:F3}ms，"
            + $"最大总计 {maximumEndToEnd:F3}ms");
        return 0;
    }

    private static int FloorImage(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Get(options, "image", string.Empty);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("floor-image 需要有效的 --image。", imagePath);
        using var source = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidOperationException("无法读取楼层回放图像。");

        Mat? cropped = null;
        var input = source;
        try
        {
            if (!Flag(options, "full"))
            {
                var settingsPath = Get(
                    options,
                    "settings",
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "IDVBuff",
                        "MapRuntime",
                        "settings.json"));
                var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
                    File.ReadAllText(settingsPath),
                    JsonOptions) ?? new MapRuntimeSettings();
                settings.Normalize();
                var region = settings.FloorDisplayRegion
                    ?? throw new InvalidOperationException(
                        "运行设置中没有楼层显示区；可用 --full 跳过裁剪。");
                var left = Math.Clamp(
                    (int)Math.Floor(region.X * source.Width),
                    0,
                    source.Width - 1);
                var top = Math.Clamp(
                    (int)Math.Floor(region.Y * source.Height),
                    0,
                    source.Height - 1);
                var right = Math.Clamp(
                    (int)Math.Ceiling(
                        (region.X + region.Width) * source.Width),
                    left + 1,
                    source.Width);
                var bottom = Math.Clamp(
                    (int)Math.Ceiling(
                        (region.Y + region.Height) * source.Height),
                    top + 1,
                    source.Height);
                cropped = new Mat(
                    source,
                    new Rect(left, top, right - left, bottom - top));
                input = cropped;
            }

            using var recognizer = new FloorIndicatorRecognizer(
                Get(
                    options,
                    "first",
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Assets",
                        "1F.png")),
                Get(
                    options,
                    "second",
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Assets",
                        "2F.png")));
            var result = recognizer.Recognize(input);
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    result.Succeeded,
                    Floor = result.Floor?.ToString(),
                    result.Confidence,
                    result.LocalizationConfidence,
                    result.LocalizedRegion,
                    result.Contrast,
                    result.AnalysisMilliseconds,
                    result.FailureReason
                },
                JsonOptions));
            return result.Succeeded ? 0 : 1;
        }
        finally
        {
            cropped?.Dispose();
        }
    }

    private static int GatesImage(
        IReadOnlyDictionary<string, string> options)
    {
        var imagePath = Get(options, "image", string.Empty);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException(
                "gates-image requires a valid --image.",
                imagePath);
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (image.Empty())
            throw new InvalidOperationException("Unable to read gate replay image.");

        var gatePath = Get(
            options,
            "gate",
            Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png"));
        using var detector = new GateTemplateDetector(gatePath);
        using var matchImage = GateTemplateDetector.CreateMatchImage(image);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var detections = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            Double(options, "client-width", 2560d),
            Double(
                options,
                "threshold",
                MapRecognitionTuning.DefaultGateTemplateThreshold));
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                Count = detections.Count,
                AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds,
                Gates = detections.Select(gate => new
                {
                    gate.Score,
                    gate.Scale,
                    gate.ScreenBounds
                })
            },
            JsonOptions));
        return detections.Count >= 2 ? 0 : 1;
    }

    private static int Match(IReadOnlyDictionary<string, string> options)
    {
        var sample = LoadSampleOrArguments(options);
        if (string.IsNullOrWhiteSpace(sample.ReferencePath)
            || string.IsNullOrWhiteSpace(sample.ScreenshotPath))
        {
            throw new ArgumentException("match 需要 --reference 与 --screenshot，或 --sample。");
        }
        using var reference = Cv2.ImRead(sample.ReferencePath, ImreadModes.Unchanged);
        using var live = Cv2.ImRead(sample.ScreenshotPath, ImreadModes.Unchanged);
        if (reference.Empty() || live.Empty())
            throw new InvalidOperationException("无法读取参考地图或局部截图。");
        var scale = Double(options, "scale", sample.History?.Scale ?? 1d);
        var offsetX = Double(options, "offset-x", sample.History?.OffsetX ?? 0d);
        var offsetY = Double(options, "offset-y", sample.History?.OffsetY ?? 0d);
        var viewport = sample.Viewport.IsValid
            ? sample.Viewport
            : new MapScreenRect(
                Double(options, "viewport-x", 0d),
                Double(options, "viewport-y", 0d),
                live.Width,
                live.Height);
        var debug = Get(
            options,
            "debug",
            Path.Combine(
                Environment.CurrentDirectory,
                "alignment-debug",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
        var enableDebug = !Flag(options, "no-debug");
        var globalSearch = Flag(options, "global");
        var preprocessor = new MapStructurePreprocessor();
        var downscaleFactor = EffectiveDownscaleFactor(
            Double(options, "downscale", 1d));
        using var liveForProcess = DownscaleImage(
            live, downscaleFactor, out var _);
        var lockedScale = scale * downscaleFactor;
        var scaledViewport = new MapScreenRect(
            0d, 0d, liveForProcess.Width, liveForProcess.Height);

        using var preparedReference = preprocessor.ProcessCachedReference(
            reference, sample.ReferencePath, out var _, out var _);
        using var preparedLive = preprocessor.ProcessLiveRoi(
            liveForProcess,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            generateVisibleMask: Flag(options, "visible"));
        preparedReference.GetOrCreateReferenceDistanceMap();
        preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            EnableDebugOutput = enableDebug,
            EnableEccRefinement = Flag(options, "ecc"),
            EnableFastAlignment = Flag(options, "fast"),
            EnableVisibleMask = Flag(options, "visible"),
            EnableVisibleAwareInjection = Flag(options, "visible"),
            EnableVisibleAwareEarlyExit = Flag(options, "visible"),
            VisibleAwareEarlyTerminationMaxCompositeCost = 0.55d,
            ScaleSearchRadius = Double(options, "search-radius", 0.15d),
            ScaleSearchStep = Double(options, "search-step", 0.01d),
            ReusePreviousAlignmentResult = Flag(options, "reuse"),
            PreviousAlignmentSearchRadiusPixels =
                (int)Double(options, "reuse-radius", 96d),
            TopCandidateCount = (int)Double(options, "top-candidates", 6d)
        };
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
                OffsetX = offsetX * downscaleFactor,
                OffsetY = offsetY * downscaleFactor,
                ReferenceWidth = reference.Width,
                ReferenceHeight = reference.Height,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            AllowScaleSearch = globalSearch || Flag(options, "allow-scale"),
            RestrictSearchToLockedTransform = !globalSearch,
            TrackingMode = !globalSearch,
            ForceBestCandidate = Flag(options, "force-best"),
            DebugOutputDirectory = enableDebug ? debug : null,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive
        });
        var document = ProbeResult.From(result, downscaleFactor);
        var output = Get(options, "out", Path.Combine(debug, "result.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(document, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return result.Accepted ? 0 : 1;
    }

    private static int Label(IReadOnlyDictionary<string, string> options)
    {
        var samplePath = Required(options, "sample");
        var sample = JsonSerializer.Deserialize<ProbeSample>(
            File.ReadAllText(samplePath),
            JsonOptions) ?? throw new InvalidOperationException("样本 JSON 无效。");
        using var reference = Cv2.ImRead(sample.ReferencePath, ImreadModes.Color);
        using var screenshot = Cv2.ImRead(sample.ScreenshotPath, ImreadModes.Color);
        if (reference.Empty() || screenshot.Empty())
            throw new InvalidOperationException("无法读取样本图片。");

        var state = new LabelState
        {
            Scale = sample.Truth?.Scale ?? sample.History?.Scale ?? 1d,
            OffsetX = sample.Truth?.OffsetX
                ?? sample.History?.OffsetX
                ?? sample.Viewport.X,
            OffsetY = sample.Truth?.OffsetY
                ?? sample.History?.OffsetY
                ?? sample.Viewport.Y,
            Alpha = 0.45d
        };
        const string windowName = "IDVB Alignment Label";
        Cv2.NamedWindow(windowName, WindowFlags.Normal);
        MouseCallback callback = (@event, x, y, flags, _) =>
        {
            if (@event == MouseEventTypes.LButtonDown)
            {
                state.Dragging = true;
                state.LastX = x;
                state.LastY = y;
            }
            else if (@event == MouseEventTypes.MouseMove && state.Dragging)
            {
                state.OffsetX += x - state.LastX;
                state.OffsetY += y - state.LastY;
                state.LastX = x;
                state.LastY = y;
            }
            else if (@event == MouseEventTypes.LButtonUp)
            {
                state.Dragging = false;
            }
        };
        Cv2.SetMouseCallback(windowName, callback);
        Console.WriteLine("拖动地图；WASD 微移；J/L 缩放；[/] 透明度；Enter 保存；Esc 取消。");
        while (true)
        {
            using var view = RenderLabel(reference, screenshot, sample.Viewport, state);
            Cv2.ImShow(windowName, view);
            var key = Cv2.WaitKeyEx(20);
            switch (key)
            {
                case 27:
                    Cv2.DestroyWindow(windowName);
                    return 1;
                case 13:
                case 10:
                    sample.Truth = new ProbeTransform
                    {
                        Scale = state.Scale,
                        OffsetX = state.OffsetX,
                        OffsetY = state.OffsetY
                    };
                    File.WriteAllText(
                        samplePath,
                        JsonSerializer.Serialize(sample, JsonOptions));
                    Cv2.DestroyWindow(windowName);
                    Console.WriteLine("真值已保存。");
                    return 0;
                case 'a':
                case 'A':
                    state.OffsetX -= 1d;
                    break;
                case 'd':
                case 'D':
                    state.OffsetX += 1d;
                    break;
                case 'w':
                case 'W':
                    state.OffsetY -= 1d;
                    break;
                case 's':
                case 'S':
                    state.OffsetY += 1d;
                    break;
                case 'j':
                case 'J':
                    state.Scale = Math.Max(0.05d, state.Scale - 0.001d);
                    break;
                case 'l':
                case 'L':
                    state.Scale += 0.001d;
                    break;
                case '[':
                    state.Alpha = Math.Max(0.05d, state.Alpha - 0.05d);
                    break;
                case ']':
                    state.Alpha = Math.Min(0.95d, state.Alpha + 0.05d);
                    break;
            }
        }
    }

    private static int Batch(IReadOnlyDictionary<string, string> options)
    {
        var directory = Path.GetFullPath(Required(options, "dir"));
        var samples = Directory.GetFiles(
            directory,
            "sample.json",
            SearchOption.AllDirectories);
        var records = new List<object>();
        var accepted = 0;
        var falseAccepts = 0;
        foreach (var path in samples)
        {
            var sample = JsonSerializer.Deserialize<ProbeSample>(
                File.ReadAllText(path),
                JsonOptions);
            if (sample is null
                || !File.Exists(sample.ReferencePath)
                || !File.Exists(sample.ScreenshotPath))
            {
                continue;
            }
            var history = sample.History
                ?? (sample.Truth is { } truth
                    ? new ProbeTransform
                    {
                        Scale = truth.Scale,
                        OffsetX = 0d,
                        OffsetY = 0d
                    }
                    : null);
            if (history is null)
                continue;
            using var reference = Cv2.ImRead(sample.ReferencePath, ImreadModes.Unchanged);
            using var live = Cv2.ImRead(sample.ScreenshotPath, ImreadModes.Unchanged);
            var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
            var result = registrar.Register(new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = sample.Viewport,
                LockedTransform = history.ToTransform(reference.Size()),
                Tuning = new MapStructureRegistrationTuning(),
                AllowScaleSearch = true
            });
            if (result.Accepted)
                accepted++;
            var error = result.Transform is not null && sample.Truth is not null
                ? TransformError(result.Transform, sample.Truth, reference.Size())
                : (double?)null;
            if (result.Accepted && error is > 4d)
                falseAccepts++;
            records.Add(new
            {
                Sample = path,
                result.Accepted,
                result.Confidence,
                Rejection = result.RejectionReason.ToString(),
                ErrorPixels = error,
                Milliseconds = result.PreprocessMilliseconds
                    + result.SearchMilliseconds
                    + result.RefineMilliseconds
            });
        }
        var summary = new
        {
            Samples = records.Count,
            Accepted = accepted,
            FalseAccepts = falseAccepts,
            Records = records
        };
        var output = Get(options, "out", Path.Combine(directory, "batch-result.json"));
        File.WriteAllText(output, JsonSerializer.Serialize(summary, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        return falseAccepts == 0 ? 0 : 1;
    }

    private static Mat RenderLabel(
        Mat reference,
        Mat screenshot,
        MapScreenRect viewport,
        LabelState state)
    {
        using var projected = new Mat();
        using var matrix = Mat.Zeros(2, 3, MatType.CV_64FC1).ToMat();
        matrix.Set(0, 0, state.Scale);
        matrix.Set(1, 1, state.Scale);
        matrix.Set(0, 2, state.OffsetX - viewport.X);
        matrix.Set(1, 2, state.OffsetY - viewport.Y);
        Cv2.WarpAffine(
            reference,
            projected,
            matrix,
            screenshot.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);
        var result = new Mat();
        Cv2.AddWeighted(
            screenshot,
            1d - state.Alpha,
            projected,
            state.Alpha,
            0d,
            result);
        Cv2.PutText(
            result,
            $"scale={state.Scale:F4} offset=({state.OffsetX:F1},{state.OffsetY:F1})",
            new Point(12, 28),
            HersheyFonts.HersheySimplex,
            0.65d,
            Scalar.Yellow,
            2);
        return result;
    }

    private static ProbeSample LoadSampleOrArguments(
        IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("sample", out var path))
        {
            return JsonSerializer.Deserialize<ProbeSample>(
                File.ReadAllText(path),
                JsonOptions) ?? new ProbeSample();
        }
        return new ProbeSample
        {
            ReferencePath = Required(options, "reference"),
            ScreenshotPath = Required(options, "screenshot")
        };
    }

    private static string ResolveSelectedReference(
        Guid? selectedMapId,
        IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("reference", out var explicitPath))
            return Path.GetFullPath(explicitPath);
        if (selectedMapId is null)
            return string.Empty;
        var mapRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVBuff",
            "Maps");
        var directory = Path.Combine(mapRoot, selectedMapId.Value.ToString("N"));
        var derived = Path.Combine(directory, "floor-1-recognition.png");
        if (File.Exists(derived))
            return derived;
        var catalogPath = Path.Combine(mapRoot, "maps.json");
        if (!File.Exists(catalogPath))
            return string.Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        foreach (var map in document.RootElement.GetProperty("Maps").EnumerateArray())
        {
            if (!Guid.TryParse(map.GetProperty("Id").GetString(), out var id)
                || id != selectedMapId)
            {
                continue;
            }
            return Path.Combine(
                directory,
                map.GetProperty("FloorOneFileName").GetString() ?? "floor-1.png");
        }
        return string.Empty;
    }

    private static bool TrySolveGateTruth(
        Guid? selectedMapId,
        IReadOnlyList<GateDetection> gates,
        MapScreenRect viewport,
        out ProbeTransform truth)
    {
        truth = new ProbeTransform();
        if (selectedMapId is null || gates.Count < 2)
            return false;
        var mapRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVBuff",
            "Maps");
        var catalogPath = Path.Combine(mapRoot, "maps.json");
        if (!File.Exists(catalogPath))
            return false;
        try
        {
            var catalog = JsonSerializer.Deserialize<MapCatalogDocument>(
                File.ReadAllText(catalogPath),
                JsonOptions);
            var map = catalog?.Maps.FirstOrDefault(item => item.Id == selectedMapId);
            if (map is null)
                return false;
            map.NormalizeRecognition();
            var profile = map.Recognition.FirstFloor;
            var main = profile.FindAnchor("main-entrance");
            var side = profile.FindAnchor("side-entrance");
            if (main?.Bounds?.IsValid is not true
                || side?.Bounds?.IsValid is not true
                || profile.RecognitionPixelWidth <= 0
                || profile.RecognitionPixelHeight <= 0)
            {
                return false;
            }
            var fingerprint = new MapGeometryFingerprint
            {
                Map = map,
                MainPoint = new MapNormalizedPoint(
                    main.Bounds.X + (main.Bounds.Width / 2d),
                    main.Bounds.Y + (main.Bounds.Height / 2d)),
                SidePoint = new MapNormalizedPoint(
                    side.Bounds.X + (side.Bounds.Width / 2d),
                    side.Bounds.Y + (side.Bounds.Height / 2d)),
                ReferenceWidth = profile.RecognitionPixelWidth,
                ReferenceHeight = profile.RecognitionPixelHeight
            };
            var candidate = MapCvRecognitionScript.RankGeometry(
                    [fingerprint],
                    gates,
                    viewport,
                    vectorErrorTolerance: 0.15d)
                .FirstOrDefault();
            if (candidate is null
                || !MapOverlayTransformSolver.TrySolve(
                    candidate,
                    MapOverlayAlignmentMode.Uniform,
                    out var transform,
                    out _))
            {
                return false;
            }
            truth = new ProbeTransform
            {
                Scale = transform.ScaleX,
                OffsetX = transform.OffsetX,
                OffsetY = transform.OffsetY
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double TransformError(
        MapOverlayTransform actual,
        ProbeTransform expected,
        Size reference)
    {
        var points = new[]
        {
            new Point2d(0d, 0d),
            new Point2d(reference.Width, 0d),
            new Point2d(0d, reference.Height),
            new Point2d(reference.Width, reference.Height),
            new Point2d(reference.Width / 2d, reference.Height / 2d)
        };
        return points.Max(point => Math.Sqrt(
            Math.Pow(
                (point.X * actual.ScaleX) + actual.OffsetX
                - ((point.X * expected.Scale) + expected.OffsetX),
                2d)
            + Math.Pow(
                (point.Y * actual.ScaleY) + actual.OffsetY
                - ((point.Y * expected.Scale) + expected.OffsetY),
                2d)));
    }

    private static async Task<int> ConfidenceReplayAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var inputPath = Required(options, "file");
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Confidence replay input was not found.", inputPath);

        var tuning = new MapStructureRegistrationTuning();
        if (options.TryGetValue("settings", out var settingsPath))
        {
            var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
                await File.ReadAllTextAsync(Path.GetFullPath(settingsPath)),
                JsonOptions) ?? new MapRuntimeSettings();
            settings.Normalize();
            tuning = settings.StructureRegistrationTuning.Clone();
        }
        tuning.Normalize();

        var minimum = Math.Clamp(Double(options, "minimum", 0.62d), 0d, 1d);
        var allowedAverageDrop = Math.Max(
            0d,
            Double(options, "max-average-drop", 0.001d));
        var rows = new List<ConfidenceReplayRow>();
        var parsedAttempts = 0;
        foreach (var line in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var attempt = JsonSerializer.Deserialize<ConfidenceReplayAttempt>(
                line,
                JsonOptions);
            if (attempt is null)
                continue;
            parsedAttempts++;
            if (!attempt.Accepted
                || attempt.ConfidenceBreakdown is not { } baseline
                || attempt.Candidates.FirstOrDefault() is not { } candidate
                || !IsConfidenceReplayEligible(candidate, baseline, tuning))
            {
                continue;
            }

            var current = MapStructureConfidenceCalculator.Calculate(
                candidate,
                baseline.CandidateSeparation,
                tuning);
            rows.Add(new ConfidenceReplayRow(
                attempt.AttemptId,
                attempt.FloorKey,
                baseline.FinalScore,
                current.LockConfidence,
                current.GeometricLockConfidence,
                current.EvidenceConfidence));
        }

        var baselineAverage = rows.Count == 0
            ? 0d
            : rows.Average(row => row.BaselineConfidence);
        var currentAverage = rows.Count == 0
            ? 0d
            : rows.Average(row => row.CurrentLockConfidence);
        var downward = rows
            .Where(row => row.BaselineConfidence >= minimum
                && row.CurrentLockConfidence < minimum)
            .Select(row => row.AttemptId)
            .ToArray();
        var upward = rows
            .Where(row => row.BaselineConfidence < minimum
                && row.CurrentLockConfidence >= minimum)
            .Select(row => row.AttemptId)
            .ToArray();
        var summary = new ConfidenceReplaySummary
        {
            InputPath = inputPath,
            ParsedAttempts = parsedAttempts,
            EligibleAttempts = rows.Count,
            MinimumLockConfidence = minimum,
            BaselineAverage = baselineAverage,
            CurrentAverage = currentAverage,
            AverageDelta = currentAverage - baselineAverage,
            BaselineBelowMinimum = rows.Count(row =>
                row.BaselineConfidence < minimum),
            CurrentBelowMinimum = rows.Count(row =>
                row.CurrentLockConfidence < minimum),
            DownwardThresholdCrossings = downward.Length,
            UpwardThresholdCrossings = upward.Length,
            MaximumAbsoluteDelta = rows.Count == 0
                ? 0d
                : rows.Max(row => Math.Abs(
                    row.CurrentLockConfidence - row.BaselineConfidence)),
            GeometricLockAverage = rows.Count == 0
                ? 0d
                : rows.Average(row => row.GeometricLockConfidence),
            EvidenceAverage = rows.Count == 0
                ? 0d
                : rows.Average(row => row.EvidenceConfidence),
            DownwardCrossingAttemptIds = downward,
            Floors = rows
                .GroupBy(row => row.FloorKey, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new ConfidenceReplayFloorSummary
                {
                    FloorKey = group.Key,
                    Attempts = group.Count(),
                    BaselineAverage = group.Average(row =>
                        row.BaselineConfidence),
                    CurrentAverage = group.Average(row =>
                        row.CurrentLockConfidence),
                    CurrentBelowMinimum = group.Count(row =>
                        row.CurrentLockConfidence < minimum)
                })
                .ToArray()
        };
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        Console.WriteLine(json);
        if (options.TryGetValue("out", out var outputPath))
        {
            var fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
            await File.WriteAllTextAsync(fullOutputPath, json);
        }

        if (!Flag(options, "fail-on-regression"))
            return 0;
        return downward.Length > 0
            || baselineAverage - currentAverage > allowedAverageDrop
            ? 1
            : 0;
    }

    private static bool IsConfidenceReplayEligible(
        MapStructureCandidate candidate,
        MapStructureConfidenceBreakdown baseline,
        MapStructureRegistrationTuning tuning) =>
        candidate.IsWithinValidBounds
        && candidate.PriorAgreement > 0.05d
        && candidate.ChamferPixels <= tuning.MaximumChamferPixels
        && candidate.EdgeCoverage >= tuning.MinimumEdgeCoverage
        && baseline.CandidateSeparation >= tuning.MinimumCandidateMargin;

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            options[key] = value;
        }
        return options;
    }

    private static string Required(
        IReadOnlyDictionary<string, string> options,
        string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"缺少 --{key}。");

    private static string Get(
        IReadOnlyDictionary<string, string> options,
        string key,
        string fallback) =>
        options.TryGetValue(key, out var value) ? value : fallback;

    private static int Int(
        IReadOnlyDictionary<string, string> options,
        string key,
        int fallback) =>
        options.TryGetValue(key, out var value)
            && int.TryParse(value, out var parsed)
                ? parsed
                : fallback;

    private static double Double(
        IReadOnlyDictionary<string, string> options,
        string key,
        double fallback) =>
        options.TryGetValue(key, out var value)
            && double.TryParse(value, out var parsed)
                ? parsed
                : fallback;

    private static bool Flag(
        IReadOnlyDictionary<string, string> options,
        string key) =>
        options.TryGetValue(key, out var value)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();
        var phase = System.Diagnostics.Stopwatch.StartNew();

        var imagePath = Required(options, "image");
        using var fullImage = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (fullImage.Empty())
            throw new InvalidOperationException("无法读取游戏截图。");
        var loadMs = phase.Elapsed.TotalMilliseconds;

        var useFullFrame = Flag(options, "full");
        var viewportMargin = Double(
            options, "viewport-margin", 0.20d);
        NormalizedRectangle? viewportRegion = null;
        if (!useFullFrame)
        {
            if (options.TryGetValue("viewport", out var raw))
            {
                var parts = raw.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 4
                    && double.TryParse(parts[0], out var vx)
                    && double.TryParse(parts[1], out var vy)
                    && double.TryParse(parts[2], out var vw)
                    && double.TryParse(parts[3], out var vh))
                {
                    viewportRegion = new NormalizedRectangle
                        { X = vx, Y = vy, Width = vw, Height = vh };
                }
            }
            else
            {
                var settingsPath = Get(
                    options,
                    "settings",
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "IDVBuff",
                        "MapRuntime",
                        "settings.json"));
                if (File.Exists(settingsPath))
                {
                    var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
                        File.ReadAllText(settingsPath),
                        JsonOptions) ?? new MapRuntimeSettings();
                    settings.Normalize();
                    if (settings.IsMapViewportCalibrated
                        && settings.MapViewportRegion is not null)
                    {
                        viewportRegion = settings.MapViewportRegion;
                        if (!options.ContainsKey("viewport-margin"))
                        {
                            viewportMargin =
                                settings.StructureRegistrationTuning
                                    .MapViewportEdgeMargin;
                        }
                    }
                }
            }
        }

        var clientWidth = Double(options, "client-width", 2560d);
        Mat screenshot;
        MapScreenRect viewport;
        if (viewportRegion is not null)
        {
            var region = viewportRegion;
            // Expand each edge outward by the margin proportion.
            var marginW = region.Width * viewportMargin;
            var marginH = region.Height * viewportMargin;
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
                (int)Math.Ceiling(
                    (expanded.X + expanded.Width) * fullImage.Width),
                left + 1, fullImage.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling(
                    (expanded.Y + expanded.Height) * fullImage.Height),
                top + 1, fullImage.Height);
            screenshot = new Mat(
                fullImage,
                new Rect(left, top, right - left, bottom - top));
            viewport = new MapScreenRect(
                0d, 0d, screenshot.Width, screenshot.Height);
            var cropMs = phase.Elapsed.TotalMilliseconds;
            loadMs += cropMs;
        }
        else
        {
            screenshot = fullImage;
            viewport = new MapScreenRect(
                0d, 0d, screenshot.Width, screenshot.Height);
        }

        var gatePath = Get(
            options,
            "gate",
            Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png"));
        using var detector = new GateTemplateDetector(gatePath);
        phase.Restart();
        using var matchImage = GateTemplateDetector.CreateMatchImage(screenshot);
        var gateMatchMs = phase.Elapsed.TotalMilliseconds;

        var threshold = Double(
            options,
            "threshold",
            MapRecognitionTuning.DefaultGateTemplateThreshold);

        phase.Restart();
        var gates = detector.Detect(
            matchImage,
            viewport,
            clientWidth,
            threshold);
        var gateDetectMs = phase.Elapsed.TotalMilliseconds;

        var repository = new MapRepository();
        phase.Restart();
        var maps = await repository.GetMapsAsync();
        var catalogMs = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        foreach (var map in maps)
            map.NormalizeRecognition();
        var fingerprints = maps
            .Select(BuildFingerprint)
            .Where(fingerprint => fingerprint is not null)
            .Cast<MapGeometryFingerprint>()
            .ToArray();
        var fingerprintMs = phase.Elapsed.TotalMilliseconds;

        if (fingerprints.Length == 0)
            throw new InvalidOperationException("没有可识别的地图。");

        var structure = Flag(options, "structure");
        var topCount = Int(options, "top", structure ? 3 : 1);

        phase.Restart();
        var ranked = MapCvRecognitionScript.RankGeometry(
            fingerprints,
            gates,
            viewport)
            .Take(topCount)
            .ToArray();
        var geometryMs = phase.Elapsed.TotalMilliseconds;

        var viewportDescription = useFullFrame
            ? "full frame"
            : viewportRegion is not null
                ? $"{viewportRegion.X:P0},{viewportRegion.Y:P0} "
                    + $"{viewportRegion.Width:P0}x{viewportRegion.Height:P0}"
                : "none (full frame fallback)";

        var candidates = new List<object>();
        foreach (var candidate in ranked)
        {
            var map = candidate.Fingerprint.Map;
            phase.Restart();
            var recognitionPath = repository.GetFloorOneRecognitionPath(map);
            using var reference = !File.Exists(recognitionPath)
                ? null
                : Cv2.ImRead(recognitionPath, ImreadModes.Unchanged);
            var referenceLoadMs = phase.Elapsed.TotalMilliseconds;

            if (reference is null || reference.Empty())
            {
                candidates.Add(new
                {
                    map.Id,
                    map.DisplayName,
                    Floor = "First",
                    candidate.VectorError,
                    candidate.Score,
                    candidate.EstimatedScaleX,
                    candidate.EstimatedScaleY,
                    ReferencePath = recognitionPath,
                    ReferenceMissing = true,
                    ReferenceLoadMs = referenceLoadMs
                });
                continue;
            }

            object? structureResult = null;
            double referenceDiskMs = 0d;
            double referencePreprocessMs = 0d;
            double livePreprocessMs = 0d;
            double distanceMapMs = 0d;
            double searchMs = 0d;
            double refineMs = 0d;
            double structureOverheadMs = 0d;
            double structureWallMs = 0d;
            object? referencePreprocessReport = null;
            object? livePreprocessReport = null;
            bool referenceCacheHit = false;
            if (structure)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var preprocessor = new MapStructurePreprocessor();
                var tuning = new MapStructureRegistrationTuning
                {
                    SchemaVersion =
                        MapStructureRegistrationTuning.CurrentSchemaVersion,
                    EnableEccRefinement = Flag(options, "ecc"),
                    TopCandidateCount = Int(options, "top-candidates", 6)
                };

                // ── downscale live ROI ──
                var downscaleFactor = EffectiveDownscaleFactor(
                    Double(options, "downscale", 0.5d));
                var ds = System.Diagnostics.Stopwatch.StartNew();
                using var liveForProcess = DownscaleImage(
                    screenshot, downscaleFactor, out var _);
                var downscaleWallMs = ds.Elapsed.TotalMilliseconds;

                // ── reference preprocess (with optional disk cache) ──
                // NOTE: preparedReference is NOT disposed here because
                // the cache owns it when cacheHit==true.
                ds.Restart();
                var preparedReference = preprocessor.ProcessCachedReference(
                    reference, recognitionPath,
                    out var refTiming, out referenceCacheHit);
                referencePreprocessMs = ds.Elapsed.TotalMilliseconds;
                referencePreprocessReport = refTiming.ToReport();

                // ── live preprocess ──
                ds.Restart();
                using var preparedLive = preprocessor.ProcessLiveRoiDiagnostic(
                    liveForProcess, out var liveTiming);
                livePreprocessMs = ds.Elapsed.TotalMilliseconds;
                livePreprocessReport = liveTiming.ToReport();

                // ── distance map ──
                ds.Restart();
                preparedReference.GetOrCreateReferenceDistanceMap();
                preparedReference.GetOrCreateClippedReferenceDistanceMap(12d);
                distanceMapMs = ds.Elapsed.TotalMilliseconds;

                // Reference-to-screen transforms shrink with the live image.
                var uniformScale = (candidate.EstimatedScaleX
                    + candidate.EstimatedScaleY) / 2d;
                var lockedScale = uniformScale * downscaleFactor;
                var scaledViewport = new MapScreenRect(
                    0d, 0d, liveForProcess.Width, liveForProcess.Height);

                // ── structure registration ──
                var registrar = new MapStructureRegistrar(preprocessor);
                var result = registrar.Register(
                    new MapStructureRegistrationRequest
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
                        AllowScaleSearch = true,
                        RestrictSearchToLockedTransform = true,
                        TrackingMode = true,
                        ForceBestCandidate = Flag(options, "force-best"),
                        PreparedReference = preparedReference,
                        PreparedLive = preparedLive
                    });
                searchMs = result.SearchMilliseconds;
                refineMs = result.RefineMilliseconds;
                structureWallMs = sw.Elapsed.TotalMilliseconds;
                structureOverheadMs = structureWallMs
                    - referencePreprocessMs - livePreprocessMs
                    - distanceMapMs - searchMs - refineMs;
                referenceDiskMs = referenceLoadMs;

                structureResult = new
                {
                    result.Accepted,
                    result.Confidence,
                    Scale = result.Transform?.ScaleX / downscaleFactor,
                    OffsetX = result.Transform?.OffsetX / downscaleFactor,
                    OffsetY = result.Transform?.OffsetY / downscaleFactor,
                    result.BestScore,
                    result.CandidateMargin,
                    Rejection = result.RejectionReason.ToString(),
                    // Wall-clock breakdown
                    WallMs = structureWallMs,
                    ReferenceDiskMs = referenceDiskMs,
                    ReferencePreprocessMs = referencePreprocessMs,
                    ReferenceCacheHit = referenceCacheHit,
                    LivePreprocessMs = livePreprocessMs,
                    LiveDownscaleMs = downscaleWallMs,
                    LiveDownscaleFactor = downscaleFactor,
                    DistanceMapMs = distanceMapMs,
                    SearchMs = searchMs,
                    RefineMs = refineMs,
                    OverheadMs = structureOverheadMs,
                    // Optional diagnostic sub-timing
                    ReferenceDetail = referencePreprocessReport,
                    LiveDetail = livePreprocessReport
                };
            }

            totalTimer.Stop();
            candidates.Add(new
            {
                map.Id,
                map.DisplayName,
                Floor = "First",
                candidate.VectorError,
                candidate.Score,
                candidate.EstimatedScaleX,
                candidate.EstimatedScaleY,
                ReferencePath = recognitionPath,
                ReferenceLoadMs = referenceLoadMs,
                Gates = new
                {
                    Main = new
                    {
                        candidate.MainGate.Score,
                        candidate.MainGate.Scale,
                        candidate.MainGate.ScreenBounds
                    },
                    Side = new
                    {
                        candidate.SideGate.Score,
                        candidate.SideGate.Scale,
                        candidate.SideGate.ScreenBounds
                    }
                },
                Structure = structureResult
            });
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                Image = Path.GetFileName(imagePath),
                ImageSize = new { fullImage.Width, fullImage.Height },
                Viewport = viewportDescription,
                CropSize = new { screenshot.Width, screenshot.Height },
                GateCount = gates.Count,
                MapCount = fingerprints.Length,
                TotalMs = totalTimer.Elapsed.TotalMilliseconds,
                Phases = new
                {
                    LoadMs = loadMs,
                    GateMatchMs = gateMatchMs,
                    GateDetectMs = gateDetectMs,
                    CatalogMs = catalogMs,
                    FingerprintMs = fingerprintMs,
                    GeometryMs = geometryMs
                },
                Candidates = candidates
            },
            JsonOptions));
        return candidates.Count > 0 ? 0 : 1;
    }

    private static readonly Dictionary<string, Mat> _referenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Mat LoadCachedReference(string path, out double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_referenceCache)
        {
            if (_referenceCache.TryGetValue(path, out var cached)
                && !cached.Empty())
            {
                ms = sw.Elapsed.TotalMilliseconds;
                return cached;
            }
        }
        var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
        ms = sw.Elapsed.TotalMilliseconds;
        if (!mat.Empty())
        {
            lock (_referenceCache)
            {
                _referenceCache[path] = mat;
            }
        }
        return mat;
    }

    private static Mat DownscaleImage(
        Mat source, double factor, out double elapsedMs)
    {
        if (factor <= 0d || factor >= 1d)
        {
            elapsedMs = 0d;
            return source.Clone();
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));
        var scaled = new Mat();
        Cv2.Resize(
            source, scaled, new Size(width, height),
            interpolation: InterpolationFlags.Area);
        elapsedMs = timer.Elapsed.TotalMilliseconds;
        return scaled;
    }

    private static double EffectiveDownscaleFactor(double factor) =>
        double.IsFinite(factor) && factor > 0d && factor < 1d
            ? factor
            : 1d;

    private static async Task<int> StatsAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var repeat = Int(options, "repeat", 10);
        var structure = Flag(options, "structure");
        if (!structure)
        {
            Console.Error.WriteLine("stats 需要 --structure。");
            return 1;
        }
        // Force known settings for reproducibility.
        var skipGates = Flag(options, "skip-gates");
        var downstreamOptions = new Dictionary<string, string>(
            options, StringComparer.OrdinalIgnoreCase)
        {
            ["full"] = "true",
            ["structure"] = "true",
            ["force-best"] = "true",
            ["skip-gates"] = skipGates ? "true" : "false"
        };
        // First run to populate any caches (cold).
        var coldSamples = new List<Dictionary<string, double>>();
        var coldStart = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < repeat; i++)
        {
            var data = await RunOnceAndCollectAsync(downstreamOptions);
            coldSamples.Add(data);
        }
        coldStart.Stop();
        // Second run with warm caches.
        var warmSamples = new List<Dictionary<string, double>>();
        var warmStart = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < repeat; i++)
        {
            var data = await RunOnceAndCollectAsync(downstreamOptions);
            warmSamples.Add(data);
        }
        warmStart.Stop();

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                Repeat = repeat,
                Cold = BuildStats("cold (first-run, no disk cache)", coldSamples,
                    coldStart.Elapsed.TotalMilliseconds),
                Warm = BuildStats("warm (in-process cache hit)", warmSamples,
                    warmStart.Elapsed.TotalMilliseconds)
            },
            JsonOptions));
        return 0;
    }

    private static async Task<Dictionary<string, double>> RunOnceAndCollectAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();

        var imagePath = Required(options, "image");
        using var fullImage = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        double loadMs = total.Elapsed.TotalMilliseconds;

        var repository = new MapRepository();
        var mapsPhase = System.Diagnostics.Stopwatch.StartNew();
        var maps = await repository.GetMapsAsync();
        foreach (var map in maps)
            map.NormalizeRecognition();
        double catalogMs = mapsPhase.Elapsed.TotalMilliseconds;

        MapGeometryCandidate candidate;
        double gateMs;
        var skipGates = Flag(options, "skip-gates");
        if (skipGates)
        {
            gateMs = 0d;
            var fingerprints = maps
                .Select(BuildFingerprint)
                .Where(f => f is not null)
                .Cast<MapGeometryFingerprint>()
                .ToArray();
            // Use the first ready fingerprint when skipping gate detection.
            var first = fingerprints[0];
            candidate = new MapGeometryCandidate
            {
                Fingerprint = first,
                MainGate = new GateDetection(),
                SideGate = new GateDetection(),
                EstimatedScaleX = 1d,
                EstimatedScaleY = 1d,
                Score = 1d
            };
        }
        else
        {
            var gatePath = Path.Combine(
                AppContext.BaseDirectory, "Assets", "Gate.png");
            using var detector = new GateTemplateDetector(gatePath);
            using var matchImage = GateTemplateDetector.CreateMatchImage(fullImage);
            var viewport = new MapScreenRect(
                0d, 0d, fullImage.Width, fullImage.Height);
            var gatePhase = System.Diagnostics.Stopwatch.StartNew();
            var gates = detector.Detect(
                matchImage, viewport, 2560d,
                MapRecognitionTuning.DefaultGateTemplateThreshold);
            gateMs = gatePhase.Elapsed.TotalMilliseconds;

            var fingerprints = maps
                .Select(BuildFingerprint)
                .Where(f => f is not null)
                .Cast<MapGeometryFingerprint>()
                .ToArray();
            var ranked = MapCvRecognitionScript.RankGeometry(
                fingerprints, gates, viewport)
                .Take(3)
                .ToArray();
            candidate = ranked[0];
        }
        var mapRecord = candidate.Fingerprint.Map;
        var recognitionPath = repository.GetFloorOneRecognitionPath(mapRecord);

        var reference = LoadCachedReference(
            recognitionPath, out var refDiskMs);

        // ── structure pipeline ──
        var structSw = System.Diagnostics.Stopwatch.StartNew();
        var preprocessor = new MapStructurePreprocessor();
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion
        };

        var downscale = System.Diagnostics.Stopwatch.StartNew();
        using var liveForProcess = DownscaleImage(
            fullImage, 0.5d, out var _);
        double downscaleMs = downscale.Elapsed.TotalMilliseconds;

        downscale.Restart();
        var preparedRef = preprocessor.ProcessCachedReference(
            reference, recognitionPath,
            out var refTiming, out var cacheHit);
        double refPreprocMs = downscale.Elapsed.TotalMilliseconds;

        downscale.Restart();
        using var preparedLive = preprocessor.ProcessLiveRoiDiagnostic(
            liveForProcess, out var liveTiming);
        double livePreprocMs = downscale.Elapsed.TotalMilliseconds;

        downscale.Restart();
        preparedRef.GetOrCreateReferenceDistanceMap();
        preparedRef.GetOrCreateClippedReferenceDistanceMap(12d);
        double distMapMs = downscale.Elapsed.TotalMilliseconds;

        var uniformScale = (candidate.EstimatedScaleX
            + candidate.EstimatedScaleY) / 2d;
        var lockedScale = uniformScale * 0.5d;
        var scaledViewport = new MapScreenRect(
            0d, 0d, liveForProcess.Width, liveForProcess.Height);

        var registrar = new MapStructureRegistrar(preprocessor);
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = liveForProcess,
                ViewportBounds = scaledViewport,
                LockedTransform = new MapOverlayTransform
                {
                    ScaleX = lockedScale,
                    ScaleY = lockedScale,
                    ReferenceWidth = reference.Width,
                    ReferenceHeight = reference.Height,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                },
                Tuning = tuning,
                AllowScaleSearch = true,
                RestrictSearchToLockedTransform = true,
                TrackingMode = true,
                ForceBestCandidate = true,
                PreparedReference = preparedRef,
                PreparedLive = preparedLive
            });
        double searchMs = result.SearchMilliseconds;
        double refineMs = result.RefineMilliseconds;
        double structWallMs = structSw.Elapsed.TotalMilliseconds;

        return new Dictionary<string, double>
        {
            ["TotalWallMs"] = total.Elapsed.TotalMilliseconds,
            ["LoadMs"] = loadMs,
            ["GateMs"] = gateMs,
            ["CatalogMs"] = catalogMs,
            ["RefDiskMs"] = refDiskMs,
            ["RefPreprocMs"] = refPreprocMs,
            ["RefCacheHit"] = cacheHit ? 1d : 0d,
            ["LivePreprocMs"] = livePreprocMs,
            ["DownscaleMs"] = downscaleMs,
            ["DistMapMs"] = distMapMs,
            ["SearchMs"] = searchMs,
            ["RefineMs"] = refineMs,
            ["StructWallMs"] = structWallMs
        };
    }

    private static object BuildStats(
        string label,
        List<Dictionary<string, double>> samples,
        double totalWall)
    {
        var keys = samples[0].Keys.ToArray();
        var result = new Dictionary<string, object>
        {
            ["Label"] = label,
            ["Samples"] = samples.Count,
            ["TotalWallMs"] = totalWall
        };
        foreach (var key in keys)
        {
            var values = samples
                .Select(s => s.GetValueOrDefault(key, 0d))
                .OrderBy(v => v)
                .ToArray();
            result[key] = new
            {
                Avg = Math.Round(values.Average(), 2),
                Min = Math.Round(values[0], 2),
                Max = Math.Round(values[^1], 2),
                P50 = Math.Round(
                    values[Math.Min(values.Length - 1, values.Length / 2)], 2),
                P95 = Math.Round(
                    values[Math.Min(values.Length - 1,
                        (int)Math.Ceiling(values.Length * 0.95d) - 1)], 2),
                P99 = Math.Round(
                    values[Math.Min(values.Length - 1,
                        (int)Math.Ceiling(values.Length * 0.99d) - 1)], 2)
            };
        }
        return result;
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
                main.Bounds.X + (main.Bounds.Width / 2d),
                main.Bounds.Y + (main.Bounds.Height / 2d)),
            SidePoint = new MapNormalizedPoint(
                side.Bounds.X + (side.Bounds.Width / 2d),
                side.Bounds.Y + (side.Bounds.Height / 2d)),
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

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            IDVBuff.MapAlignment.Probe

              capture [--delay 3] [--out DIR] [--reference FILE]
                      [--history previous-sample.json]
              floor   [--delay 3] [--repeat 100]
                      [--first Assets/1F.png] [--second Assets/2F.png]
                      [--out first-captured-region.png]
              floor-image --image FILE [--settings settings.json | --full]
              gates-image --image FILE [--client-width 2560] [--threshold 0.72]
              side-scan --image FILE [--top 10]
              label   --sample sample.json
              match   (--sample sample.json | --reference FILE --screenshot FILE)
                      --scale N [--offset-x N --offset-y N] [--allow-scale]
                      [--prepared-reference] [--prepared-live] [--top-candidates N]
                      [--reuse] [--reuse-radius SCREEN_PIXELS]
                      [--force-best]
                      [--ecc] [--visible] [--no-debug] [--debug DIR] [--out result.json]
              batch   --dir SAMPLE_DIR [--out batch-result.json]
              confidence-replay --file attempts.jsonl [--settings settings.json]
                      [--minimum 0.62] [--out result.json]
                      [--fail-on-regression] [--max-average-drop 0.001]
              run     --image FILE [--structure] [--ecc] [--force-best]
                      [--top N] [--top-candidates N]
                      [--client-width 2560] [--threshold 0.72]
            """);
    }

    private sealed class LabelState
    {
        public double Scale { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double Alpha { get; set; }
        public bool Dragging { get; set; }
        public int LastX { get; set; }
        public int LastY { get; set; }
    }

    private sealed record ConfidenceReplayRow(
        Guid AttemptId,
        string FloorKey,
        double BaselineConfidence,
        double CurrentLockConfidence,
        double GeometricLockConfidence,
        double EvidenceConfidence);

    private sealed class ConfidenceReplayAttempt
    {
        public Guid AttemptId { get; init; }
        public string FloorKey { get; init; } = string.Empty;
        public IReadOnlyList<MapStructureCandidate> Candidates { get; init; } = [];
        public MapStructureConfidenceBreakdown? ConfidenceBreakdown { get; init; }
        public bool Accepted { get; init; }
    }

    private sealed class ConfidenceReplaySummary
    {
        public string InputPath { get; init; } = string.Empty;
        public int ParsedAttempts { get; init; }
        public int EligibleAttempts { get; init; }
        public double MinimumLockConfidence { get; init; }
        public double BaselineAverage { get; init; }
        public double CurrentAverage { get; init; }
        public double AverageDelta { get; init; }
        public int BaselineBelowMinimum { get; init; }
        public int CurrentBelowMinimum { get; init; }
        public int DownwardThresholdCrossings { get; init; }
        public int UpwardThresholdCrossings { get; init; }
        public double MaximumAbsoluteDelta { get; init; }
        public double GeometricLockAverage { get; init; }
        public double EvidenceAverage { get; init; }
        public IReadOnlyList<Guid> DownwardCrossingAttemptIds { get; init; } = [];
        public IReadOnlyList<ConfidenceReplayFloorSummary> Floors { get; init; } = [];
    }

    private sealed class ConfidenceReplayFloorSummary
    {
        public string FloorKey { get; init; } = string.Empty;
        public int Attempts { get; init; }
        public double BaselineAverage { get; init; }
        public double CurrentAverage { get; init; }
        public int CurrentBelowMinimum { get; init; }
    }
}

internal sealed class ProbeSample
{
    public Guid? MapId { get; set; }
    public string ReferencePath { get; set; } = string.Empty;
    public string ScreenshotPath { get; set; } = string.Empty;
    public MapScreenRect Viewport { get; set; }
    public MapScreenRect Client { get; set; }
    public IReadOnlyList<ProbeGate> Gates { get; set; } = [];
    public ProbeTransform? History { get; set; }
    public ProbeTransform? Truth { get; set; }
    public MapNormalizedPoint? PlayerScreenPoint { get; set; }
}

internal sealed class ProbeGate
{
    public double Score { get; set; }
    public double Scale { get; set; }
    public MapScreenRect Bounds { get; set; }
}

internal sealed class ProbeTransform
{
    public double Scale { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public MapOverlayTransform ToTransform(Size reference) => new()
    {
        ScaleX = Scale,
        ScaleY = Scale,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        ReferenceWidth = reference.Width,
        ReferenceHeight = reference.Height,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };
}

internal sealed class ProbeResult
{
    public bool Accepted { get; init; }
    public double? Scale { get; init; }
    public double? OffsetX { get; init; }
    public double? OffsetY { get; init; }
    public double Confidence { get; init; }
    public double BestScore { get; init; }
    public double? SecondScore { get; init; }
    public double CandidateMargin { get; init; }
    public string RejectionReason { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public IReadOnlyList<MapStructureCandidate> TopCandidates { get; init; } = [];
    public object Query { get; init; } = new();
    public object Timings { get; init; } = new();

    public static ProbeResult From(
        MapStructureRegistrationResult result,
        double coordinateScale = 1d)
    {
        var normalization = double.IsFinite(coordinateScale)
                            && coordinateScale > 0d
            ? coordinateScale
            : 1d;
        var candidates = normalization == 1d
            ? result.Candidates
            : result.Candidates
                .Select(candidate => candidate with
                {
                    Scale = candidate.Scale / normalization,
                    OffsetX = candidate.OffsetX / normalization,
                    OffsetY = candidate.OffsetY / normalization
                })
                .ToArray();

        return new ProbeResult
        {
            Accepted = result.Accepted,
            Scale = result.Transform?.ScaleX / normalization,
            OffsetX = result.Transform?.OffsetX / normalization,
            OffsetY = result.Transform?.OffsetY / normalization,
            Confidence = result.Confidence,
            BestScore = result.BestScore,
            SecondScore = double.IsFinite(result.SecondScore)
                ? result.SecondScore
                : null,
            CandidateMargin = result.CandidateMargin,
            RejectionReason = result.RejectionReason.ToString(),
            FailureReason = result.FailureReason,
            TopCandidates = candidates,
            Query = new
            {
                LockedScale = result.LockedScale / normalization,
                ReferenceSize = new
                {
                    Width = result.ReferenceWidth,
                    Height = result.ReferenceHeight
                },
                EdgePixels = result.QueryEdgePixels,
                Bounds = new
                {
                    X = result.QueryBoundsX,
                    Y = result.QueryBoundsY,
                    Width = result.QueryBoundsWidth,
                    Height = result.QueryBoundsHeight
                },
                result.ScaleHypothesisCount,
                result.OversizedHypothesisCount,
                result.UsedRestrictedSearch,
                result.WasForcedBestCandidate,
                result.VisibleFraction,
                result.VisibleStructurePixels,
                result.VisibleEdgePixels,
                result.VisibleAwareCandidateCount,
                VisibleAwareTopCost = double.IsFinite(result.VisibleAwareTopCost)
                    ? result.VisibleAwareTopCost
                    : (double?)null,
                VisibleAwareTopMargin = double.IsFinite(result.VisibleAwareTopMargin)
                    ? result.VisibleAwareTopMargin
                    : (double?)null,
                result.VisibleAwareEarlyAccepted,
                result.VisibleAwareFallbackReason
            },
            Timings = new
            {
                PreprocessMilliseconds = result.PreprocessMilliseconds,
                SearchMilliseconds = result.SearchMilliseconds,
                RefineMilliseconds = result.RefineMilliseconds,
                result.VisibleMaskMilliseconds,
                result.VisibleAwareSearchMilliseconds
            }
        };
    }
}
