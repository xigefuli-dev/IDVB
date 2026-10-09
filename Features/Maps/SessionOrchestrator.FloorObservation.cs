using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Task _floorObservationTask = Task.CompletedTask;

    private sealed record FloorObservationContext(
        MapMatchSnapshot Match,
        MapGameToggleTransition Toggle,
        Guid MapId,
        DateTimeOffset MapUpdatedAt,
        long OperationGeneration,
        string FloorKey,
        IntPtr Window,
        MapScreenRect ClientBounds,
        (double X, double Y, double Width, double Height) Viewport);

    private sealed record FloorObservationProposal(
        FloorObservationContext Context, string Floor, FloorIndicatorMatchResult Result);

    private async Task RunFloorIndicatorObservationAsync(CancellationToken cancellationToken)
    {
        var stability = new MapFloorStabilityTracker();
        FloorObservationContext? previous = null;
        FloorObservationProposal? pending = null;
        string? lastError = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                var identity = _pendingAlignmentIdentity ?? _lastRecognition;
                if (_disposed || IsMatchEnding || _settings?.IsEnabled != true
                    || _settings.DisableAutoFloor || !_gameMapToggleState.IsOpen
                    || identity is null || !EnsureOverlayCaptureExclusion(
                        "Floor observation stopped because display capture protection is unavailable.",
                        "显示层捕获保护不可用。"))
                {
                    stability.Reset();
                    previous = null;
                    Interlocked.Exchange(ref pending, null);
                    continue;
                }

                var group = FloorIndicatorTemplateRegistry.Resolve(
                    MapFloorRules.GetOrderedFloors(identity.Map).Select(floor => floor.Key));
                if (group is null || !_captureSvc.TryGetForegroundClientBounds(
                    out var boundsObject, out var window, out _) || boundsObject is not MapScreenRect bounds)
                {
                    stability.Reset();
                    previous = null;
                    Interlocked.Exchange(ref pending, null);
                    continue;
                }

                var viewport = ResolveMapViewportForCurrentWindow();
                var context = new FloorObservationContext(_matchSession.Snapshot,
                    new MapGameToggleTransition(true, _gameMapToggleState.Version), identity.Map.Id,
                    identity.Map.UpdatedAt, Volatile.Read(ref _currentMapOpenGeneration),
                    _currentFloorKey ?? identity.Result.Floor, window, bounds,
                    (viewport.X, viewport.Y, viewport.Width, viewport.Height));
                if (previous != context)
                {
                    stability.Reset();
                    Interlocked.Exchange(ref pending, null);
                    previous = context;
                }

                var region = FloorIndicatorCaptureRegion.Above(viewport, group);
                if (!_captureSvc.TryCaptureUiRegion(region, out var capturedObject, out _)
                    || capturedObject is not CapturedGameFrame captured)
                {
                    (capturedObject as IDisposable)?.Dispose();
                    stability.Reset();
                    continue;
                }

                using (captured)
                {
                    lastError = null;
                    if (captured.WindowHandle != window || captured.ClientBounds != bounds)
                    {
                        stability.Reset();
                        continue;
                    }
                    var result = FloorIndicatorTemplateRegistry.RecognizeDetailed(group, captured.Image,
                        FloorIndicatorCaptureRegion.TemplateScale(group, captured.ClientBounds));
                    if (!result.Succeeded || result.DetectedFloor is not { } floor)
                    {
                        stability.Reset();
                        continue;
                    }
                    if (!stability.Observe(floor, Stopwatch.GetTimestamp(),
                            (long)(Stopwatch.Frequency * MapFloorRecognitionRules.ConfirmationSampleIntervalMilliseconds / 1000d))
                        || floor == context.FloorKey)
                        continue;

                    // Never await the dispatcher here. Re-alignment cancels
                    // tracking; the observer must remain independent of it.
                    var proposal = new FloorObservationProposal(context, floor, result);
                    if (Interlocked.CompareExchange(ref pending, proposal, null) is not null)
                        continue;
                    if (!_dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                RunInputAction("observed-floor-switch", () =>
                                    ApplyObservedFloor(proposal.Context, proposal.Floor, proposal.Result));
                            }
                            finally { Interlocked.CompareExchange(ref pending, null, proposal); }
                        }))
                        Interlocked.CompareExchange(ref pending, null, proposal);
                    else
                        _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                            "常开楼层候选已入队",
                            details: new()
                            {
                                ["mapId"] = context.MapId, ["fromFloor"] = context.FloorKey,
                                ["toFloor"] = floor, ["operationGeneration"] = context.OperationGeneration,
                                ["score"] = result.BestScore, ["margin"] = result.Margin
                            });
                }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    stability.Reset();
                    previous = null;
                    Interlocked.Exchange(ref pending, null);
                    var error = exception.GetType().FullName + ":" + exception.Message;
                    if (lastError != error)
                        _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Warning,
                            $"持续楼层观察暂时失败，将继续观察：{exception.Message}",
                            details: new() { ["exception"] = exception.ToString() });
                    lastError = error;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ApplyObservedFloor(FloorObservationContext context, string floor,
        FloorIndicatorMatchResult result)
    {
        var identity = _pendingAlignmentIdentity ?? _lastRecognition;
        var viewport = ResolveMapViewportForCurrentWindow();
        if (_disposed || _settings?.IsEnabled != true || _settings.DisableAutoFloor
            || !IsCurrentMatchOperation(context.Match) || !_gameMapToggleState.IsCurrent(context.Toggle)
            || context.OperationGeneration != Volatile.Read(ref _currentMapOpenGeneration)
            || identity?.Map.Id != context.MapId || identity.Map.UpdatedAt != context.MapUpdatedAt
            || (_currentFloorKey ?? identity.Result.Floor) != context.FloorKey
            || (viewport.X, viewport.Y, viewport.Width, viewport.Height) != context.Viewport
            || !_captureSvc.TryGetForegroundClientBounds(out var boundsObject, out var window, out _)
            || window != context.Window || boundsObject is not MapScreenRect bounds || bounds != context.ClientBounds)
            return;

        var floors = MapFloorRules.GetOrderedFloors(identity.Map);
        var group = FloorIndicatorTemplateRegistry.Resolve(floors.Select(item => item.Key));
        if (group is null || !_captureSvc.TryCaptureUiRegion(
                FloorIndicatorCaptureRegion.Above(viewport, group), out var currentObject, out _))
            return;
        using var current = currentObject as CapturedGameFrame;
        if (current is null || current.WindowHandle != context.Window
            || current.ClientBounds != context.ClientBounds
            || FloorIndicatorTemplateRegistry.RecognizeDetailed(group, current.Image,
                FloorIndicatorCaptureRegion.TemplateScale(group, current.ClientBounds)).DetectedFloor != floor)
            return;
        var position = floors.ToList().FindIndex(item => item.Key == floor);
        if (position < 0 || floor == context.FloorKey)
            return;
        var decision = MapFloorSwitchDecision.AtPosition(identity.Map, context.FloorKey, position + 1);
        if (!decision.Succeeded)
            return;
        _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
            $"常开地图检出楼层变化：{context.FloorKey} → {floor}",
            details: new()
            {
                ["source"] = "continuous-game-indicator", ["mapId"] = context.MapId,
                ["fromFloor"] = context.FloorKey, ["toFloor"] = floor,
                ["score"] = result.BestScore, ["margin"] = result.Margin,
                ["matchMs"] = result.MatchMilliseconds
            });
        ApplyFloorSwitch(identity, _pendingAlignmentIdentity is null ? "aligned" : "pending-alignment",
            context.Match.Version, decision, "continuous-game-indicator", realignWhileOpen: true);
    }
}
