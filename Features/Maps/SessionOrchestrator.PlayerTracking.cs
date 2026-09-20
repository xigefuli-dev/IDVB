using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly object _livePlayerTrackingGate = new();
    private CancellationTokenSource? _livePlayerTrackingCts;
    private Task? _livePlayerTrackingTask;

    private void StartLivePlayerTracking(
        RuntimeMapRecognition recognition,
        CapturedGameFrame seedFrame)
    {
        CancelLivePlayerTracking("new_tracking_started");

        if (!_gameMapToggleState.IsOpen
            || recognition.Result.OverlayTransform is not { } transform
            || !Lifecycle.MainProgramPreferences.Load().EnhancedMiniMapEnabled)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            CurrentMatchCancellationToken,
            _lifetimeCts.Token);

        lock (_livePlayerTrackingGate)
        {
            _livePlayerTrackingCts = cts;
            _livePlayerTrackingTask = Task.Run(
                () => RunLivePlayerTrackingLoopAsync(recognition, transform, cts.Token));
        }
    }

    private void CancelLivePlayerTracking(string reason)
    {
        lock (_livePlayerTrackingGate)
        {
            if (_livePlayerTrackingCts is not null)
            {
                try
                {
                    _livePlayerTrackingCts.Cancel();
                    _livePlayerTrackingCts.Dispose();
                }
                catch { }
                finally
                {
                    _livePlayerTrackingCts = null;
                }
            }
        }
    }

    private async Task RunLivePlayerTrackingLoopAsync(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform,
        CancellationToken cancellationToken)
    {
        using var d1 = new MapPlayerMarkerDetector();
        using var d2 = new MapPlayerMarkerDetector();
        using var d3 = new MapPlayerMarkerDetector();
        using var d4 = new MapPlayerMarkerDetector();

        var detectorMap = new Dictionary<PlayerSlot, (MapPlayerMarkerDetector Detector, string Path)>
        {
            [PlayerSlot.Player1] = (d1, MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player1)),
            [PlayerSlot.Player2] = (d2, MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player2)),
            [PlayerSlot.Player3] = (d3, MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player3)),
            [PlayerSlot.Player4] = (d4, MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player4)),
        };

        var activeSlots = detectorMap
            .Where(pair => File.Exists(pair.Value.Path))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        if (activeSlots.Count == 0) return;

        var previousPoints = new Dictionary<PlayerSlot, MapViewportPoint>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_gameMapToggleState.IsOpen || cancellationToken.IsCancellationRequested)
                    break;

                var viewportRegion = ResolveMapViewportForCurrentWindow();
                if (!_captureSvc.TryCaptureViewport(viewportRegion, out var frameObj, out _)
                    || frameObj is not CapturedGameFrame frame)
                {
                    continue;
                }

                using (frame)
                {
                    if (!_gameMapToggleState.IsOpen || cancellationToken.IsCancellationRequested)
                        break;

                    var candidates = new List<(MiniMapTrackedPlayer Player, MapViewportPoint Pt, double Conf)>();
                    foreach (var (slot, (det, path)) in activeSlots)
                    {
                        previousPoints.TryGetValue(slot, out var prev);
                        var detection = det.Detect(
                            frame.Image,
                            frame.ViewportBounds,
                            frame.ClientBounds,
                            slot,
                            path,
                            prev);

                        if (detection.Succeeded)
                        {
                            previousPoints[slot] = detection.ViewportPoint;

                            var refX = (detection.ScreenPoint.X - transform.OffsetX) / transform.ScaleX;
                            var refY = (detection.ScreenPoint.Y - transform.OffsetY) / transform.ScaleY;

                            if (transform.ReferenceWidth > 0 && transform.ReferenceHeight > 0)
                            {
                                var normX = Math.Clamp(refX / transform.ReferenceWidth, 0.0, 1.0);
                                var normY = Math.Clamp(refY / transform.ReferenceHeight, 0.0, 1.0);
                                candidates.Add((new MiniMapTrackedPlayer(slot, normX, normY, DateTimeOffset.UtcNow),
                                    detection.ViewportPoint, detection.Confidence));
                            }
                        }
                    }

                    // 空间重叠抑制（NMS）：距离小于 24px 时只保留置信度更高的 slot，避免同点重复抢夺
                    var filtered = new List<MiniMapTrackedPlayer>();
                    foreach (var c in candidates.OrderByDescending(x => x.Conf))
                    {
                        var conflict = filtered.Any(f =>
                        {
                            var existing = candidates.First(x => x.Player.Slot == f.Slot);
                            var dx = c.Pt.X - existing.Pt.X;
                            var dy = c.Pt.Y - existing.Pt.Y;
                            return (dx * dx + dy * dy) < 576.0;
                        });
                        if (!conflict) filtered.Add(c.Player);
                    }

                    if (filtered.Count > 0)
                    {
                        _dispatcher.TryEnqueue(() => _overlay.UpdateMiniMapPlayers(filtered));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                $"大地图实时玩家位置追踪异常退出：{ex.Message}");
        }
    }
}
