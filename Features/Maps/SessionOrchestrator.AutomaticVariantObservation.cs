using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Task _automaticVariantObservationTask = Task.CompletedTask;

    private sealed record AutomaticVariantObservationContext(
        MapMatchSnapshot Match, MapGameToggleTransition Toggle, Guid GroupId,
        Guid MapId, DateTimeOffset MapUpdatedAt, string Floor, long Generation,
        MapCatalogRevision CatalogRevision, IntPtr Window, MapScreenRect Client,
        (double X, double Y, double Width, double Height) Viewport);

    // Observe pixels only. The dispatched request reuses the normal capture,
    // complete group comparison and identity/display writers under _scanGate.
    // Never wait on the dispatcher here: match shutdown drains this loop.
    private async Task RunAutomaticVariantObservationAsync(CancellationToken cancellationToken)
    {
        AutomaticVariantObservationContext? previous = null;
        Mat? previousEdges = null;
        var pending = 0;
        var retryCapture = 0;
        string? lastError = null;
        using var scratch = new Vpsg3LiveExtractorScratch();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(350));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var identity = _pendingAlignmentIdentity ?? _lastRecognition;
                    var group = _mapLease.VariantIdentity;
                    if (_disposed || IsMatchEnding || _settings?.IsEnabled != true
                        || !_gameMapToggleState.IsOpen || identity is null
                        || group is null || group.ConfirmedMemberId is not null)
                    {
                        previous = null;
                        previousEdges?.Dispose();
                        previousEdges = null;
                        continue;
                    }
                    if (Volatile.Read(ref pending) != 0 || _scanGate.CurrentCount == 0
                        || !EnsureOverlayCaptureExclusion(
                            "Variant observation requires protected game capture.",
                            "显示层捕获保护不可用。")) continue;
                    if (Interlocked.Exchange(ref retryCapture, 0) != 0)
                    {
                        previousEdges?.Dispose();
                        previousEdges = null;
                    }

                    var viewport = AutomaticMapViewport();
                    if (!_captureSvc.TryGetForegroundClientBounds(out var clientObject,
                            out var window, out _) || clientObject is not MapScreenRect client)
                        continue;
                    var context = new AutomaticVariantObservationContext(_matchSession.Snapshot,
                        new MapGameToggleTransition(true, _gameMapToggleState.Version), group.GroupId,
                        identity.Map.Id, identity.Map.UpdatedAt, _currentFloorKey ?? identity.Result.Floor,
                        Volatile.Read(ref _currentMapOpenGeneration), _mapRepository.GetCatalogRevision(),
                        window, client, (viewport.X, viewport.Y, viewport.Width, viewport.Height));
                    // Our own comparison advances the operation generation.
                    // That invalidates queued work, not the unchanged pixel
                    // baseline; otherwise each pass would schedule itself again.
                    if (previous is null || (previous with { Generation = context.Generation }) != context)
                    {
                        previousEdges?.Dispose();
                        previousEdges = null;
                    }
                    previous = context;
                    if (!_captureSvc.TryCaptureViewport(viewport, out var capturedObject, out _)
                        || capturedObject is not CapturedGameFrame captured)
                    {
                        (capturedObject as IDisposable)?.Dispose();
                        continue;
                    }
                    using (captured)
                    {
                        if (captured.WindowHandle != window || captured.ClientBounds != client) continue;
                        using var observation = Vpsg3FastLiveExtractor.Extract(captured.Image,
                            captured.ViewportBounds, scratch: scratch,
                            excludedScreenRegions: MapFrameUiExclusion.AutomaticCanvas(client));
                        var edges = observation.ProposalEdges;
                        // Exact semantic-edge equality saves duplicate computation.
                        // No coarse score or hash can hide a newly exposed corner.
                        if (previousEdges is not null && previousEdges.Size() == edges.Size()
                            && Cv2.Norm(previousEdges, edges, NormTypes.INF) == 0) continue;
                        previousEdges?.Dispose();
                        previousEdges = edges.Clone();
                    }
                    if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) continue;
                    if (!_dispatcher.TryEnqueue(async () =>
                    {
                        try
                        {
                            if (cancellationToken.IsCancellationRequested
                                || !IsAutomaticVariantObservationCurrent(context)
                                || _scanGate.CurrentCount == 0)
                            {
                                Interlocked.Exchange(ref retryCapture, 1);
                                return;
                            }
                            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
                                "常开地图发现新结构，自动重新判断相似组", details: new()
                                {
                                    ["groupId"] = context.GroupId, ["mapId"] = context.MapId,
                                    ["source"] = "continuous-variant-observation"
                                });
                            await RunSingleMapOpenAlignmentAsync(context.Toggle, independentAlignment: false);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception exception)
                        {
                            Interlocked.Exchange(ref retryCapture, 1);
                            if (!cancellationToken.IsCancellationRequested && !_disposed)
                                _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning,
                                    $"持续相似组判断暂未完成：{exception.Message}");
                        }
                        finally { Interlocked.Exchange(ref pending, 0); }
                    }))
                    {
                        Interlocked.Exchange(ref pending, 0);
                        previousEdges?.Dispose();
                        previousEdges = null;
                    }
                    else
                        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
                            "常开相似组判断已入队", details: new()
                            {
                                ["groupId"] = context.GroupId, ["mapId"] = context.MapId,
                                ["toggleVersion"] = context.Toggle.Version
                            });
                    lastError = null;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    previous = null;
                    previousEdges?.Dispose();
                    previousEdges = null;
                    var error = exception.GetType().FullName + ":" + exception.Message;
                    if (lastError != error)
                        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning,
                            $"持续相似组观察暂时失败，将继续观察：{exception.Message}");
                    lastError = error;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { previousEdges?.Dispose(); }
    }

    private bool IsAutomaticVariantObservationCurrent(AutomaticVariantObservationContext context)
    {
        var identity = _pendingAlignmentIdentity ?? _lastRecognition;
        var group = _mapLease.VariantIdentity;
        var viewport = AutomaticMapViewport();
        return !_disposed && !IsMatchEnding && _settings?.IsEnabled == true
            && IsCurrentMatchOperation(context.Match) && _gameMapToggleState.IsCurrent(context.Toggle)
            && context.Generation == Volatile.Read(ref _currentMapOpenGeneration)
            && identity?.Map.Id == context.MapId && identity.Map.UpdatedAt == context.MapUpdatedAt
            && (_currentFloorKey ?? identity.Result.Floor) == context.Floor
            && group?.GroupId == context.GroupId && group.ConfirmedMemberId is null
            && _mapRepository.GetCatalogRevision() == context.CatalogRevision
            && (viewport.X, viewport.Y, viewport.Width, viewport.Height) == context.Viewport
            && _captureSvc.TryGetForegroundClientBounds(out var clientObject, out var window, out _)
            && window == context.Window && clientObject is MapScreenRect client && client == context.Client;
    }
}
