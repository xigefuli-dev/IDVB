using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static AdaptiveAlignmentDecision ReferencePythonAlignmentDecision(RuntimeMapRecognition recognition) =>
        new(recognition, AdaptiveScaleReliability.Reliable,
            AllowLegacyCacheWrite: false, AllowReliableSession: true, AllowHotStartMemory: true,
            StartOrbTracking: true, Status: "ReferencePython", ConsecutiveHighQualityCount: 0,
            RequiredHighQualityCount: 0, ReliabilityReason: AdaptiveScaleReliabilityReason.None,
            InitialScaleRelativeMad: 0, InitialScaleClusterRebuilt: false);

    private ReferencePythonPose? CreateReferencePythonPrior(MapMatchSnapshot match,
        CapturedGameFrame frame, MapRecord map, string floor)
    {
        // Reuse the existing match/map/floor/resolution-scoped store. Python never
        // owns the committed pose and cannot learn a seed from a cancelled reply.
        var seed = TryGetReliableFloorAlignment(match, frame, map, floor, out _);
        if (seed is null || !seed.SourceClientBounds.IsValid
            || !double.IsFinite(seed.SourceClientBounds.X) || !double.IsFinite(seed.SourceClientBounds.Y))
            return null;
        var transform = seed.Session.LockedTransform;
        return new ReferencePythonPose
        {
            MapId = map.Id, Floor = floor, Trusted = true,
            Scale = transform.ScaleX,
            // The cached screen transform belongs to its capture's window
            // origin; a later move must not translate the client-space seed.
            Tx = transform.OffsetX - seed.SourceClientBounds.X,
            Ty = transform.OffsetY - seed.SourceClientBounds.Y
        };
    }

    private async Task RunReferencePythonMapOpenAlignmentAsync(MapGameToggleTransition toggle,
        MapMatchSnapshot match, RuntimeMapRecognition locked, CancellationToken token,
        bool independentAlignment)
    {
        var timer = Stopwatch.StartNew();
        var trace = ActiveOperationTrace;
        var context = CaptureMapOpenOperationContext(toggle, match, locked, token, _currentFloorKey);
        var recoveringIdentity = _pendingAlignmentIdentity is not null;
        var primaryFloor = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var floor = _currentFloorKey ?? primaryFloor;
        trace?.SetContext(route: "reference-python", mapId: locked.Map.Id.ToString("D"), floorKey: floor);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(1000);
        CapturedGameFrame? frame = null;
        MapRecognitionAttempt? attempt = null;
        try
        {
            var floors = MapFloorRules.GetOrderedFloors(locked.Map).Select(item => item.Key).ToArray();
            var group = FloorIndicatorTemplateRegistry.Resolve(floors);
            if (group is null)
                throw new InvalidDataException("Python 验证版当前仅支持已注册的困难双楼层资源。");
            var autoFloor = new AutoFloorCapture(locked.Map, group);
            var captureStarted = timer.Elapsed.TotalMilliseconds;
            using (trace?.StartTopLevel("stable_viewport", MapOperationWaitKind.Capture,
                mapId: locked.Map.Id.ToString("D"), floorKey: floor))
                frame = await CaptureStableViewportAsync("Python 仅对齐", deadline.Token,
                    relaxForLockedMap: true, shouldContinue: () => IsMapOpenOperationCurrent(context),
                    autoFloor: autoFloor);
            var captureMs = timer.Elapsed.TotalMilliseconds - captureStarted;
            if (frame is null || !IsMapOpenOperationCurrent(context))
            {
                trace?.SetTerminal(frame is null ? "failed" : "superseded", "reference-python-capture");
                frame?.Dispose();
                frame = null;
                return;
            }
            floor = FloorRecognitionRules.ResolveTargetFloor(context.IsManualFloor, _currentFloorKey,
                frame.DetectedFloorKey, primaryFloor);
            trace?.SetContext(floorKey: floor);
            if (!context.IsManualFloor && frame.DetectedFloorKey is null)
            {
                attempt = MapCvRecognitionDiagnostics.Failure(
                    MapCvRecognitionDiagnostics.CreateDiagnostics(_recognition.ReadyMapCount, _recognition.TotalMapCount),
                    "原引擎等待本帧明确楼层，不使用历史楼层替代。");
            }
            else
            {
                var prior = independentAlignment ? null : CreateReferencePythonPrior(match, frame, locked.Map, floor);
                using (trace?.StartTopLevel("alignment_compute", MapOperationWaitKind.Compute,
                    mapId: locked.Map.Id.ToString("D"), floorKey: floor))
                    attempt = await _recognition.AlignReferencePythonAsync(frame, locked.Map, floor, prior,
                        Math.Max(1, 1000 - (int)timer.ElapsedMilliseconds - 20), deadline.Token);
                attempt.Diagnostics.WarmStateHit = prior is not null;
                attempt.Diagnostics.WarmStateMissReason = prior is null ? "missing-state" : string.Empty;
                attempt.Diagnostics.AlignmentClass = prior is null ? "Initial" : "Steady";
                attempt.Diagnostics.StableViewportWaitMilliseconds = captureMs;
                attempt.Diagnostics.StableViewportMode = "reference-python-client-frame";
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            trace?.SetTerminal("failed", "reference-python-deadline");
            attempt = MapCvRecognitionDiagnostics.Failure(
                MapCvRecognitionDiagnostics.CreateDiagnostics(_recognition.ReadyMapCount, _recognition.TotalMapCount),
                "Python 原引擎本轮超过 1000ms，保留地图身份等待下一帧。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            trace?.SetTerminal("failed", "reference-python-error");
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Error,
                "Python 原引擎调用失败", details: new() { ["exception"] = error.ToString() });
            attempt = MapCvRecognitionDiagnostics.Failure(
                MapCvRecognitionDiagnostics.CreateDiagnostics(_recognition.ReadyMapCount, _recognition.TotalMapCount),
                error.Message);
        }
        finally
        {
            // Publication below owns a successful capture until its final UI commit.
            if (token.IsCancellationRequested)
            {
                frame?.Dispose();
                frame = null;
            }
        }
        token.ThrowIfCancellationRequested();
        if (attempt is null) { frame?.Dispose(); return; }
        _lastDiagnostics = attempt.Diagnostics;
        if (frame is null)
        {
            if (IsMapOpenOperationCurrent(context))
            {
                _statusMessage = attempt.FailureReason;
                _overlay.ClearMap();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        using (frame)
        {
            var outcome = await PublishMapOpenAlignmentResultAsync(toggle, match, frame, locked, floor,
                recoveringIdentity, attempt.Recognition, attempt.FailureReason, null, false, context);
            if (outcome != MapOpenAlignmentPublishOutcome.Succeeded)
                trace?.SetTerminal(outcome == MapOpenAlignmentPublishOutcome.Superseded ? "superseded" : "failed",
                    "reference-python-alignment-not-accepted");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
