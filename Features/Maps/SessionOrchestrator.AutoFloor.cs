using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private CapturedGameFrame ExtractCapturedAlignmentFrame(MapRecord map, CapturedGameFrame captured)
    {
        var viewport = ResolveMapViewportForCurrentWindow();
        var floors = MapFloorRules.GetOrderedFloors(map).Select(f => f.Key).ToArray();
        var group = FloorIndicatorTemplateRegistry.Resolve(floors);
        if (group is not null && _settings?.DisableAutoFloor != true)
            return new AutoFloorCapture(map, group).Extract(captured, viewport);

        var bounds = DwrGameWindowCaptureService.GetViewportBounds(captured.ClientBounds, viewport);
        var rect = new Rect((int)Math.Round(bounds.X - captured.ViewportBounds.X),
            (int)Math.Round(bounds.Y - captured.ViewportBounds.Y),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
        if ((rect & new Rect(0, 0, captured.Image.Width, captured.Image.Height)) != rect)
            throw new InvalidDataException("Map viewport is outside the captured scan frame.");
        return new CapturedGameFrame(new Mat(captured.Image, rect), captured.ClientBounds,
            bounds, captured.WindowHandle)
        {
            DetectedFloorKey = captured.DetectedFloorKey,
            UiExclusionRegions = captured.UiExclusionRegions
        };
    }

    private Task<CapturedGameFrame?> CaptureBackgroundAlignmentFrameAsync(
        MapRecord map, CancellationToken cancellationToken, Func<bool> shouldContinue)
    {
        var orderedFloors = MapFloorRules.GetOrderedFloors(map).Select(f => f.Key).ToArray();
        var group = FloorIndicatorTemplateRegistry.Resolve(orderedFloors);
        if (orderedFloors.Length <= 1)
        {
            _logCollector.Append(
                MapLogCategory.FloorRecognition,
                MapLogLevel.Info,
                $"后台扫描：当前地图为单楼层地图，无需自动楼层检测 · map={(string.IsNullOrWhiteSpace(map.Title) ? map.Id.ToString("D") : map.Title)} · floor={MapFloorRules.GetPrimaryFloorKey(map)}",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floorCount"] = orderedFloors.Length,
                    ["primaryFloor"] = MapFloorRules.GetPrimaryFloorKey(map)
                });
        }
        else if (group is null && _settings?.DisableAutoFloor != true)
        {
            _logCollector.Append(
                MapLogCategory.FloorRecognition,
                MapLogLevel.Warning,
                $"后台扫描：多楼层地图未匹配到适用的楼层指示器模板组，跳过自动楼层检测 · map={(string.IsNullOrWhiteSpace(map.Title) ? map.Id.ToString("D") : map.Title)} · floors=[{string.Join(",", orderedFloors)}]",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floors"] = orderedFloors
                });
        }

        var autoFloor = group is null || _settings?.DisableAutoFloor == true
            ? null : new AutoFloorCapture(map, group);
        if (autoFloor is not null)
        {
            var initialViewport = ResolveMapViewportForCurrentWindow();
            autoFloor.LogMonitoringStarted(initialViewport, "后台扫描消费对齐");
        }

        return CaptureStableViewportAsync("后台扫描消费对齐", cancellationToken,
            relaxForLockedMap: group is not null, shouldContinue: shouldContinue,
            autoFloor: autoFloor);
    }

    internal sealed class AutoFloorCapture(MapRecord map, FloorIndicatorTemplateRegistry.Group group)
    {
        private bool _summaryLogged;

        public MapRecord Map { get; } = map;
        public FloorIndicatorTemplateRegistry.Group Group { get; } = group;
        public string? FloorKey { get; private set; }
        public double Score { get; private set; }
        public double Margin { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public double LatestExtractMilliseconds { get; private set; }
        public double LatestMatchMilliseconds { get; private set; }
        public FloorIndicatorMatchResult? LatestMatchResult { get; private set; }
        public string LatestFailureReason { get; private set; } = string.Empty;
        public int AttemptCount { get; private set; }
        public int SucceededAttempts { get; private set; }
        public NormalizedRectangle? ViewportRegion { get; private set; }
        public NormalizedRectangle? IndicatorRegion { get; private set; }
        public MapScreenRect ClientBounds { get; private set; }
        public MapScreenRect IndicatorScreenBounds { get; private set; }
        public Rect IndicatorFrameRect { get; private set; }
        public Rect CapturedImageExtent { get; private set; }

        public NormalizedRectangle Expand(NormalizedRectangle viewport) =>
            FloorIndicatorCaptureRegion.IncludeMap(viewport, Group);

        public void LogMonitoringStarted(NormalizedRectangle viewport, string sourceOperation)
        {
            var region = FloorIndicatorCaptureRegion.Above(viewport, Group);
            MapLogCollector.Instance.Append(
                MapLogCategory.FloorRecognition,
                MapLogLevel.Info,
                $"开始自动楼层区域监测 · map={(string.IsNullOrWhiteSpace(Map.Title) ? Map.Id.ToString("D") : Map.Title)} · group={Group.Key} · op={sourceOperation}",
                details: new()
                {
                    ["mapId"] = Map.Id,
                    ["mapTitle"] = Map.Title,
                    ["operation"] = sourceOperation,
                    ["groupKey"] = Group.Key,
                    ["candidateStates"] = Group.States.Keys.ToArray(),
                    ["referenceClientWidth"] = Group.ReferenceClientWidth,
                    ["groupRegionX"] = Group.X,
                    ["groupRegionY"] = Group.Y,
                    ["groupRegionWidth"] = Group.Width,
                    ["groupRegionHeight"] = Group.Height,
                    ["groupPixelWidth"] = Group.PixelWidth,
                    ["groupPixelHeight"] = Group.PixelHeight,
                    ["minScoreThreshold"] = FloorIndicatorTemplateRegistry.DefaultMinScore,
                    ["minMarginThreshold"] = FloorIndicatorTemplateRegistry.DefaultMinMargin,
                    ["viewportNormalized"] = $"X={viewport.X:F4},Y={viewport.Y:F4},W={viewport.Width:F4},H={viewport.Height:F4}",
                    ["indicatorNormalized"] = $"X={region.X:F4},Y={region.Y:F4},W={region.Width:F4},H={region.Height:F4}"
                });
        }

        public CapturedGameFrame Extract(CapturedGameFrame captured, NormalizedRectangle viewport)
        {
            var frameTimer = System.Diagnostics.Stopwatch.StartNew();
            AttemptCount++;
            ViewportRegion = viewport;
            ClientBounds = captured.ClientBounds;
            FloorKey = null;
            Score = Margin = 0;
            LatestMatchResult = null;
            LatestFailureReason = string.Empty;

            try
            {
                var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(captured.ClientBounds, viewport);
                Rect Local(MapScreenRect bounds) => new(
                    (int)Math.Round(bounds.X - captured.ViewportBounds.X),
                    (int)Math.Round(bounds.Y - captured.ViewportBounds.Y),
                    (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
                var mapRect = Local(viewportBounds);
                var extent = new Rect(0, 0, captured.Image.Width, captured.Image.Height);
                CapturedImageExtent = extent;
                if ((mapRect & extent) != mapRect)
                    throw new InvalidDataException("Map viewport is outside the capture.");

                var region = FloorIndicatorCaptureRegion.Above(viewport, Group);
                IndicatorRegion = region;
                MapScreenRect bounds = default;
                Rect rect = default;
                double scale = 0d;
                FloorIndicatorMatchResult? matchResult = null;

                if (region.Height <= 0)
                {
                    LatestFailureReason = "视口上方无有效指示器监测空间 (viewport.Y <= 0)。";
                }
                else
                {
                    bounds = DwrGameWindowCaptureService.GetViewportBounds(captured.ClientBounds, region);
                    IndicatorScreenBounds = bounds;
                    rect = Local(bounds);
                    IndicatorFrameRect = rect;

                    if ((rect & extent) != rect)
                    {
                        LatestFailureReason = $"指示器裁剪区域超出截帧边界: rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height}) vs extent=({extent.Width}x{extent.Height})。";
                    }
                    else
                    {
                        try
                        {
                            using var indicator = new Mat(captured.Image, rect);
                            if (indicator.Empty() || indicator.Width <= 0 || indicator.Height <= 0)
                            {
                                LatestFailureReason = "裁剪指示器图像为空。";
                            }
                            else
                            {
                                scale = FloorIndicatorCaptureRegion.TemplateScale(Group, captured.ClientBounds);
                                matchResult = FloorIndicatorTemplateRegistry.RecognizeDetailed(
                                    Group, indicator, scale);
                                LatestMatchResult = matchResult;
                                LatestMatchMilliseconds = matchResult.MatchMilliseconds;
                                Score = matchResult.BestScore;
                                Margin = matchResult.Margin;

                                if (matchResult.Succeeded)
                                {
                                    FloorKey = matchResult.DetectedFloor;
                                    LatestFailureReason = string.Empty;
                                    SucceededAttempts++;
                                }
                                else
                                {
                                    LatestFailureReason = matchResult.FailureDescription;
                                }
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            LatestFailureReason = $"提取楼层指示器异常: {exception.Message}";
                        }
                    }
                }

                frameTimer.Stop();
                LatestExtractMilliseconds = frameTimer.Elapsed.TotalMilliseconds;
                TotalMilliseconds += LatestExtractMilliseconds;

                var candidateScoresDict = matchResult?.CandidateScores is not null
                    ? new Dictionary<string, object?>(matchResult.CandidateScores.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, Math.Round(kvp.Value, 4))))
                    : null;

                var attemptDetails = new Dictionary<string, object?>
                {
                    ["attempt"] = AttemptCount,
                    ["succeeded"] = matchResult?.Succeeded ?? false,
                    ["detectedFloor"] = FloorKey,
                    ["bestScore"] = matchResult?.BestScore ?? 0d,
                    ["bestFloor"] = matchResult?.BestFloor,
                    ["secondScore"] = matchResult?.SecondScore ?? 0d,
                    ["secondFloor"] = matchResult?.SecondFloor,
                    ["margin"] = matchResult?.Margin ?? 0d,
                    ["minScoreThreshold"] = FloorIndicatorTemplateRegistry.DefaultMinScore,
                    ["minMarginThreshold"] = FloorIndicatorTemplateRegistry.DefaultMinMargin,
                    ["candidateScores"] = candidateScoresDict,
                    ["rejectionReason"] = matchResult?.RejectionReason ?? (string.IsNullOrEmpty(LatestFailureReason) ? "None" : "ExtractionFailed"),
                    ["failureDescription"] = LatestFailureReason,
                    ["clientBounds"] = $"{captured.ClientBounds.X},{captured.ClientBounds.Y},{captured.ClientBounds.Width}x{captured.ClientBounds.Height}",
                    ["indicatorScreenBounds"] = $"{bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}",
                    ["indicatorFrameRect"] = $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}",
                    ["templateScale"] = scale,
                    ["frameExtractMs"] = LatestExtractMilliseconds,
                    ["matchMs"] = matchResult?.MatchMilliseconds ?? 0d
                };

                MapLogCollector.Instance.Append(
                    MapLogCategory.FloorRecognition,
                    MapLogLevel.Info,
                    $"自动楼层帧监测[{AttemptCount}] · {(matchResult?.Succeeded == true ? $"检出={FloorKey} · 置信度={matchResult.BestScore:P1} · 区分度={matchResult.Margin:P1}" : $"未检出: {LatestFailureReason}")}",
                    elapsedMs: LatestExtractMilliseconds,
                    details: attemptDetails);

                // Mat ROI retains the backing buffer; no second map-sized copy.
                var extracted = new CapturedGameFrame(new Mat(captured.Image, mapRect), captured.ClientBounds,
                    viewportBounds, captured.WindowHandle)
                {
                    CaptureBackend = captured.CaptureBackend,
                    CaptureSystemRelativeTicks = captured.CaptureSystemRelativeTicks,
                    DetectedFloorKey = FloorKey,
                    UiExclusionRegions = captured.UiExclusionRegions
                };
                return extracted;
            }
            finally
            {
                captured.Dispose();
            }
        }

        public void LogSummaryResult(string? currentFloorKey, string primaryFloorKey, bool captureTimedOut = false)
        {
            if (_summaryLogged) return;
            _summaryLogged = true;

            var effectiveFallback = currentFloorKey ?? primaryFloorKey;
            var candidateScoresDict = LatestMatchResult?.CandidateScores is not null
                ? new Dictionary<string, object?>(LatestMatchResult.CandidateScores.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, Math.Round(kvp.Value, 4))))
                : null;

            if (FloorKey is { } detected)
            {
                var willSwitch = !string.Equals(detected, effectiveFallback, StringComparison.Ordinal);
                MapLogCollector.Instance.Append(
                    MapLogCategory.FloorRecognition,
                    MapLogLevel.Info,
                    $"自动楼层识别完成 · 检出={detected.ToUpperInvariant()} · 置信度={Score:P1} · 区分度={Margin:P1} · 尝试={AttemptCount}次 · 总耗时={TotalMilliseconds:F1}ms",
                    elapsedMs: TotalMilliseconds,
                    details: new()
                    {
                        ["outcome"] = "succeeded",
                        ["detectedFloor"] = detected,
                        ["currentFloor"] = currentFloorKey,
                        ["primaryFloor"] = primaryFloorKey,
                        ["willSwitchFloor"] = willSwitch,
                        ["confidence"] = Score,
                        ["margin"] = Margin,
                        ["attempts"] = AttemptCount,
                        ["succeededAttempts"] = SucceededAttempts,
                        ["totalElapsedMs"] = TotalMilliseconds,
                        ["candidateScores"] = candidateScoresDict,
                        ["indicatorRegion"] = IndicatorRegion is not null ? $"X={IndicatorRegion.X:F4},Y={IndicatorRegion.Y:F4},W={IndicatorRegion.Width:F4},H={IndicatorRegion.Height:F4}" : null,
                        ["indicatorScreenBounds"] = $"{IndicatorScreenBounds.X},{IndicatorScreenBounds.Y},{IndicatorScreenBounds.Width}x{IndicatorScreenBounds.Height}",
                        ["indicatorFrameRect"] = $"{IndicatorFrameRect.X},{IndicatorFrameRect.Y},{IndicatorFrameRect.Width}x{IndicatorFrameRect.Height}"
                    });
            }
            else
            {
                var reason = !string.IsNullOrEmpty(LatestFailureReason)
                    ? LatestFailureReason
                    : captureTimedOut
                        ? "截帧超时未获得稳定画面"
                        : "未达到楼层判定阈值";
                MapLogCollector.Instance.Append(
                    MapLogCategory.FloorRecognition,
                    MapLogLevel.Warning,
                    $"未能给出自动楼层结果 · 回退楼层={effectiveFallback} · 原因={reason} · 尝试={AttemptCount}次 · 总耗时={TotalMilliseconds:F1}ms",
                    elapsedMs: TotalMilliseconds,
                    details: new()
                    {
                        ["outcome"] = "unresolved",
                        ["fallbackFloor"] = effectiveFallback,
                        ["currentFloor"] = currentFloorKey,
                        ["primaryFloor"] = primaryFloorKey,
                        ["rejectionReason"] = LatestMatchResult?.RejectionReason ?? (captureTimedOut ? "CaptureTimeout" : "Unresolved"),
                        ["failureDescription"] = reason,
                        ["lastBestScore"] = Score,
                        ["lastMargin"] = Margin,
                        ["lastCandidateScores"] = candidateScoresDict,
                        ["attempts"] = AttemptCount,
                        ["totalElapsedMs"] = TotalMilliseconds,
                        ["captureTimedOut"] = captureTimedOut,
                        ["indicatorRegion"] = IndicatorRegion is not null ? $"X={IndicatorRegion.X:F4},Y={IndicatorRegion.Y:F4},W={IndicatorRegion.Width:F4},H={IndicatorRegion.Height:F4}" : null,
                        ["indicatorScreenBounds"] = $"{IndicatorScreenBounds.X},{IndicatorScreenBounds.Y},{IndicatorScreenBounds.Width}x{IndicatorScreenBounds.Height}",
                        ["indicatorFrameRect"] = $"{IndicatorFrameRect.X},{IndicatorFrameRect.Y},{IndicatorFrameRect.Width}x{IndicatorFrameRect.Height}"
                    });
            }
        }
    }
}
