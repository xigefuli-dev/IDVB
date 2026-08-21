// IDVB Remaster Phase 3.2 — Scan Pipeline Stages (functional implementation)

using System.Diagnostics;
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using OpenCvSharp;

namespace IDVBuff.Pipeline.Stages;

/// <summary>截图阶段 — 从游戏窗口截取视口图像。</summary>
public sealed class CaptureStage : IPipelineStage
{
    private readonly IGameWindowCapture _capture;

    public CaptureStage(IGameWindowCapture capture)
    {
        _capture = capture;
    }

    public string StageName => "capture";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        if (context is not ScanPipelineContext scanCtx)
            return context.Fail("CaptureStage requires ScanPipelineContext");

        // 若 ViewportImage 已被编排器预填充（如 SessionOrchestrator.HandleGameMapToggleAsync），
        // 跳过重复截图，避免将 MapScreenRect 错误传入 TryCaptureViewport 的 NormalizedRectangle 参数。
        if (scanCtx.ViewportImage is not null)
        {
            scanCtx.ScreenshotPath = $"capture_{context.ExecutionId}";
            await Task.CompletedTask;
            return scanCtx;
        }

        var sw = Stopwatch.StartNew();
        using var captureSpan = MapOperationTraceAmbient.StartChild(
            "pipeline_capture",
            MapOperationWaitKind.Capture);

        if (!_capture.TryGetForegroundClientBounds(out var clientBoundsObj, out var hwnd, out var reason))
            return context.Fail($"Capture failed: {reason}");

        if (!_capture.TryCaptureViewport(clientBoundsObj, out var frameObj, out reason))
            return context.Fail($"Viewport capture failed: {reason}");

        // 桥接：IGameWindowCapture 返回 object（主项目侧的实际类型是 CapturedGameFrame / Mat）
        // 尝试提取 Mat 引用（CapturedGameFrame 通常有 .Image 属性返回 Mat）
        if (frameObj is Mat mat)
        {
            scanCtx.ViewportImage = mat;
        }
        else if (frameObj is not null)
        {
            // frameObj 是 CapturedGameFrame — 通过反射获取内部 Mat（避免项目引用）
            var imageProp = frameObj.GetType().GetProperty("Image");
            if (imageProp?.GetValue(frameObj) is Mat imageMat)
                scanCtx.ViewportImage = imageMat;
            else
                scanCtx.ViewportImage = null;
        }
        else
        {
            scanCtx.ViewportImage = null;
        }

        scanCtx.ScreenshotPath = $"capture_{context.ExecutionId}";
        scanCtx.RecordPhase("capture", sw.Elapsed.TotalMilliseconds);

        await Task.CompletedTask;
        return scanCtx;
    }
}

/// <summary>楼层检测阶段 — 识别当前地图楼层（1F / 2F）。</summary>
public sealed class FloorDetectStage : IPipelineStage
{
    private readonly IFloorRecognizer _floorRecognizer;

    public FloorDetectStage(IFloorRecognizer floorRecognizer)
    {
        _floorRecognizer = floorRecognizer;
    }

    public string StageName => "floor_detect";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var floorSpan = MapOperationTraceAmbient.StartChild(
            "floor_detection",
            MapOperationWaitKind.Compute);

        if (context is ScanPipelineContext { SkipFloorDetection: true } scanCtxSkip)
        {
            context.DetectedFloor = FloorLevel.Unknown;
        }
        else if (context is ScanPipelineContext { ViewportImage: { } image })
        {
            try
            {
                // IFloorRecognizer.Recognize(Mat) 返回 object
                // 桥接：主项目侧的 FloorRecognizerAdapter 返回 FloorIndicatorClassification
                var result = _floorRecognizer.Recognize(image);
                context.DetectedFloor = MapFloorLevel(result);
            }
            catch (Exception)
            {
                context.DetectedFloor = FloorLevel.Unknown;
            }
        }
        else
        {
            context.DetectedFloor = FloorLevel.Unknown;
        }

        context.RecordPhase("floor_detect", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }

    /// <summary>
    /// 将 IFloorRecognizer 返回的 object 映射到 FloorLevel。
    /// 运行时实际类型是 FloorIndicatorClassification（来自 Features/Maps），
    /// 此处通过反射读取 .Succeeded / .Floor 属性以避免项目引用依赖。
    /// </summary>
    private static FloorLevel MapFloorLevel(object classification)
    {
        var type = classification.GetType();
        var succeeded = (bool?)type.GetProperty("Succeeded")?.GetValue(classification);
        var floor = (int?)type.GetProperty("Floor")?.GetValue(classification);

        if (succeeded != true || floor is null or < 1 or > 2)
            return FloorLevel.Unknown;

        return floor == 1 ? FloorLevel.First : FloorLevel.Second;
    }
}

/// <summary>门检测阶段 — 检测游戏画面中的大门/侧门图标。</summary>
public sealed class GateDetectStage : IPipelineStage
{
    private readonly IGateDetector _gateDetector;

    public GateDetectStage(IGateDetector gateDetector)
    {
        _gateDetector = gateDetector;
    }

    public string StageName => "gate_detect";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var gateSpan = MapOperationTraceAmbient.StartChild(
            "gate_detection",
            MapOperationWaitKind.Compute);

        if (context is ScanPipelineContext { ViewportImage: { } image } scanCtx)
        {
            try
            {
                // 使用 context 中的桥接对象（由 SessionOrchestrator 预填充）
                // 若无则回退到 default(MapScreenRect) — 适配器侧会处理
                var viewportBounds = (context as ScanPipelineContext)?.ViewportBoundsRaw
                    ?? new object(); // fallback: 适配器将收到空对象

                var clientWidth = scanCtx.ClientWidth > 0d
                    && double.IsFinite(scanCtx.ClientWidth)
                    ? scanCtx.ClientWidth
                    : 1920d;
                var threshold = scanCtx.GateTemplateThreshold > 0d
                    && double.IsFinite(scanCtx.GateTemplateThreshold)
                    ? scanCtx.GateTemplateThreshold
                    : 0.6d;
                const double fallbackPairThreshold = 0.6d;
                var detected = _gateDetector.Detect(
                    image,
                    viewportBounds,
                    clientWidth,
                    threshold);

                // 桥接：IGateDetector 返回 IReadOnlyList<object>，
                // 运行时实际类型是 IReadOnlyList<GateDetection>（来自 Features/Maps）
                if (detected is IReadOnlyList<GateDetection> typedGates)
                {
                    if (typedGates.Count < 2
                        && threshold > fallbackPairThreshold)
                    {
                        var relaxed = _gateDetector.Detect(
                            image,
                            viewportBounds,
                            clientWidth,
                            fallbackPairThreshold);
                        if (relaxed is IReadOnlyList<GateDetection> relaxedGates
                            && relaxedGates.Count > typedGates.Count)
                        {
                            typedGates = relaxedGates;
                        }
                    }

                    scanCtx.DetectedGates.AddRange(typedGates);
                }
            }
            catch
            {
                // Gate detection failed — pipeline may fallback to structure-only
            }
        }

        context.RecordPhase("gate_detect", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}

/// <summary>地图识别阶段 — 几何指纹排名 + 候选选择。</summary>
public sealed class MapIdentifyStage : IPipelineStage
{
    private readonly IMapIdentifier _mapIdentifier;
    private readonly IMapRepository _mapRepo;

    public MapIdentifyStage(IMapIdentifier mapIdentifier, IMapRepository mapRepo)
    {
        _mapIdentifier = mapIdentifier;
        _mapRepo = mapRepo;
    }

    public string StageName => "map_identify";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (context is not ScanPipelineContext scanCtx)
            return context.Fail("MapIdentifyStage requires ScanPipelineContext");

        // 指纹由 SessionOrchestrator（主项目侧）预构建并通过 context.FingerprintsRaw 传入
        var fingerprints = scanCtx.FingerprintsRaw is IReadOnlyList<object> fpList
            ? fpList
            : Array.Empty<object>();

        if (scanCtx.DetectedGates is { Count: > 0 })
        {
            try
            {
                // 将 Core.Models.GateDetection 列表桥接回 object 列表
                var gateObjects = scanCtx.DetectedGates
                    .Cast<object>()
                    .ToList() as IReadOnlyList<object>;

                var viewportBounds = scanCtx.ViewportBoundsRaw ?? new object();

                using var geometrySpan = MapOperationTraceAmbient.StartChild(
                    "geometry_ranking",
                    MapOperationWaitKind.Compute);
                var candidates = _mapIdentifier.RankGeometry(
                    fingerprints, gateObjects, viewportBounds);
                geometrySpan.Complete();

                // 运行时类型是 IReadOnlyList<MapGeometryCandidate>（来自 Features/Maps）
                // MapGeometryCandidate 的属性嵌套在 Fingerprint.Map 中，
                // 通过反射读取以填充 Core.Models.MapCandidate。
                using var candidateSpan = MapOperationTraceAmbient.StartChild(
                    "candidate_generation",
                    MapOperationWaitKind.Compute);
                scanCtx.Candidates = candidates
                    .Select((c, i) =>
                    {
                        var cType = c.GetType();
                        // Fingerprint → Map → Id / DisplayName
                        var fp = cType.GetProperty("Fingerprint")?.GetValue(c);
                        var fpType = fp?.GetType();
                        var map = fpType?.GetProperty("Map")?.GetValue(fp);
                        var mapType = map?.GetType();
                        // MainGate / SideGate → GateDetection.Score
                        var mainGate = cType.GetProperty("MainGate")?.GetValue(c);
                        var sideGate = cType.GetProperty("SideGate")?.GetValue(c);
                        var mainType = mainGate?.GetType();
                        var sideType = sideGate?.GetType();

                        return new MapCandidate
                        {
                            Rank = i + 1,
                            MapId = mapType?.GetProperty("Id")?.GetValue(map)?.ToString() ?? "",
                            MapDisplayName = mapType?.GetProperty("DisplayName")?.GetValue(map)?.ToString() ?? "",
                            FloorKey = fpType?.GetProperty("FloorKey")?.GetValue(fp)?.ToString() ?? "1f",
                            Score = (double)(cType.GetProperty("Score")?.GetValue(c) ?? 0.0),
                            VectorError = (double)(cType.GetProperty("VectorError")?.GetValue(c) ?? double.MaxValue),
                            EstimatedScaleX = (double)(cType.GetProperty("EstimatedScaleX")?.GetValue(c) ?? 0.0),
                            EstimatedScaleY = (double)(cType.GetProperty("EstimatedScaleY")?.GetValue(c) ?? 0.0),
                            MainGateScore = (double)(mainType?.GetProperty("Score")?.GetValue(mainGate) ?? 0.0),
                            SideGateScore = (double)(sideType?.GetProperty("Score")?.GetValue(sideGate) ?? 0.0),
                        };
                    })
                    .OrderBy(c => c.VectorError)
                    .ToList();
                candidateSpan.Complete();

                if (scanCtx.Candidates.Count > 0)
                {
                    scanCtx.SelectedCandidate = scanCtx.Candidates[0];
                    context.IdentifiedMapId = scanCtx.SelectedCandidate.MapId;
                }
                else
                {
                    return context.Fail("No map candidates matched");
                }
            }
            catch (Exception ex)
            {
                return context.Fail($"Map identification failed: {ex.Message}");
            }
        }
        else
        {
            return context.Fail("No gates detected — cannot identify map");
        }

        context.RecordPhase("map_identify", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}
