using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private readonly ReferencePythonEngineClient _referencePythonEngine = new();

    internal bool UsesReferencePython(MapRecord map) => ReferencePythonEngineClient.IsConfigured
        && map.Floors.Any(floor => floor.EntryIdentityAsset is not null);

    internal bool UsesReferencePython(string? mapClass) => ReferencePythonEngineClient.IsConfigured
        && _maps.Any(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase)
            && UsesReferencePython(map));

    internal Task PrepareReferencePythonAsync(CancellationToken token = default) =>
        _referencePythonEngine.PrepareAsync(token);

    private static Mat RequireReferenceClientFrame(CapturedGameFrame frame)
    {
        var image = frame.FullClientImage
            ?? (frame.ViewportBounds == frame.ClientBounds ? frame.Image : null);
        if (image is null || image.Width != (int)Math.Round(frame.ClientBounds.Width)
            || image.Height != (int)Math.Round(frame.ClientBounds.Height))
            throw new InvalidDataException("Python 引擎需要本次捕获的完整客户区画面，不能用视口或补黑边代替。");
        return image;
    }

    internal Task<ReferencePythonEngineResponse> IdentifyReferencePythonAsync(
        CapturedGameFrame frame, string mapClass, string floor, int budgetMs, CancellationToken token) =>
        _referencePythonEngine.InvokeAsync(RequireReferenceClientFrame(frame),
            new ReferencePythonEngineRequest
            {
                Operation = "identify", MapClass = mapClass, Floor = floor,
                BudgetMs = Math.Clamp(budgetMs, 1, 1000)
            }, token);

    internal async Task<MapRecognitionAttempt> AlignReferencePythonAsync(
        CapturedGameFrame frame, MapRecord map, string floorKey, ReferencePythonPose? prior,
        int budgetMs, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var revision = CatalogRevision;
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount);
        diagnostics.ScaleBootstrapMode = "ReferencePython";
        diagnostics.StructureAttempted = true;
        MapRecognitionAttempt Reject(string reason)
        {
            diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            diagnostics.StructureRejectionReason = MapStructureRejectionReason.NoCandidate;
            return MapCvRecognitionDiagnostics.Failure(diagnostics, reason);
        }
        if (map.Floors.FirstOrDefault(f => f.Key == floorKey)?.EntryIdentityAsset is not { } asset)
            return Reject("目标楼层没有小抄入口资源。");
        if (frame.DetectedFloorKey is { } observedFloor
            && MapScanFloorRules.NormalizeFloorIdentity(observedFloor)
                != MapScanFloorRules.NormalizeFloorIdentity(floorKey))
            return Reject("本帧检测楼层与目标楼层冲突，保留身份等待正确楼层。");
        var response = await _referencePythonEngine.InvokeAsync(RequireReferenceClientFrame(frame),
            new ReferencePythonEngineRequest
            {
                Operation = "align", MapId = map.Id, MapClass = map.Class, Floor = floorKey,
                PriorPose = prior, BudgetMs = Math.Clamp(budgetMs, 1, 1000)
            }, token);
        token.ThrowIfCancellationRequested();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"Python 原引擎对齐 · {response.Reason}", elapsedMs: timer.Elapsed.TotalMilliseconds,
            details: new() { ["mapId"] = map.Id, ["floor"] = floorKey,
                ["priorUsed"] = prior is not null, ["response"] = response });
        if (!revision.Equals(CatalogRevision) || !revision.Equals(Repository.GetCatalogRevision())
            || TryGetMap(map.Id)?.UpdatedAt != map.UpdatedAt)
            return Reject("Python 结果对应的地图库已变化。");
        if (!response.PoseVerified || response.MapId != map.Id
            || MapScanFloorRules.NormalizeFloorIdentity(response.Floor)
                != MapScanFloorRules.NormalizeFloorIdentity(floorKey))
            return Reject($"原引擎未确认本帧贴合：{response.Reason}");
        if (response.Scale is not { } scale || !double.IsFinite(scale) || scale <= 0
            || response.Tx is not { } tx || !double.IsFinite(tx)
            || response.Ty is not { } ty || !double.IsFinite(ty))
            return Reject("Python 引擎未返回有效的客户区变换。");

        var transform = MapCanonicalTransformMath.BuildOverlayTransform(scale, scale,
            tx + frame.ClientBounds.X, ty + frame.ClientBounds.Y, asset.SourceWidth, asset.SourceHeight);
        var confidence = response.Confidence is { } measured && double.IsFinite(measured)
            ? Math.Clamp(measured, 0, 1) : 0;
        // Preserve the original current-author residual score. This does not
        // fabricate gate matches or imply that every competing map was scanned.
        var structure = new MapStructureRegistrationResult
        {
            Accepted = true, Transform = transform, Confidence = confidence, BestScore = 1 - confidence,
            SecondScore = double.PositiveInfinity, RejectionReason = MapStructureRejectionReason.None,
            LockedScale = scale, ReferenceWidth = asset.SourceWidth, ReferenceHeight = asset.SourceHeight,
            SearchMilliseconds = timer.Elapsed.TotalMilliseconds
        };
        var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(map, floorKey,
            Repository.GetFloorOverlayPath(map, floorKey), transform, structure, 1);
        recognition.EntryCatalogRevision = revision;
        recognition.ReferencePythonValidated = true;
        diagnostics.StructureAccepted = true;
        diagnostics.StructureRejectionReason = MapStructureRejectionReason.None;
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
        diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;
        diagnostics.ScaleBootstrapMethod = response.Reason ?? "reference-python";
        diagnostics.StructureSearchMilliseconds = timer.Elapsed.TotalMilliseconds;
        diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
        return new MapRecognitionAttempt
        {
            Recognition = recognition, Diagnostics = diagnostics, StructureResult = structure,
            StructureAttempted = true, StructureAccepted = true,
            SearchStage = AlignmentSearchStage.StructureFallback
        };
    }
}
