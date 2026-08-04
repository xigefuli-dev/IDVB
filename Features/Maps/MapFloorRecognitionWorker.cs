using System.Collections.Concurrent;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// A warm single-thread worker that retries tiny floor captures while the
/// game renders the indicator. The 100ms target is diagnostic, not a cutoff.
/// </summary>
public sealed class MapFloorRecognitionWorker : IDisposable
{
    private readonly BlockingCollection<Request> _requests =
        new(new ConcurrentQueue<Request>(), boundedCapacity: 1);
    private readonly FloorIndicatorCaptureService _capture = new();
    private readonly FloorIndicatorRecognizer _recognizer;
    private readonly MapFloorStabilityTracker _presenceStability = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private int _disposeStarted;
    private int _resourcesReleased;

    public MapFloorRecognitionWorker(
        string firstFloorReferencePath,
        string secondFloorReferencePath)
    {
        _recognizer = new FloorIndicatorRecognizer(
            firstFloorReferencePath,
            secondFloorReferencePath);
        _thread = new Thread(WorkLoop)
        {
            IsBackground = true,
            Name = "IDVBuff floor recognition"
        };
        _thread.Start();
    }

    public Task<MapFloorRecognitionResult> RecognizeAsync(
        NormalizedRectangle region,
        long inputTimestamp,
        CancellationToken cancellationToken,
        MapFloorRecognitionTuning? tuning = null)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        var completion = new TaskCompletionSource<MapFloorRecognitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestTuning = tuning?.Clone() ?? new MapFloorRecognitionTuning();
        requestTuning.Normalize();
        var request = new Request(
            region.Clone(),
            inputTimestamp,
            Stopwatch.GetTimestamp(),
            cancellationToken,
            requestTuning,
            completion);
        try
        {
            if (!_requests.TryAdd(request))
            {
                if (_requests.TryTake(out var dropped))
                {
                    dropped.Completion.TrySetResult(CreateFailure(
                        dropped.InputTimestamp,
                        dropped.EnqueuedTimestamp,
                        dropped.EnqueuedTimestamp,
                        0d,
                        0d,
                        0d,
                        0,
                        "A newer floor-recognition frame replaced this queued frame."));
                }
                if (_requests.TryAdd(request))
                    return completion.Task;
                completion.TrySetResult(CreateFailure(
                    inputTimestamp,
                    0d,
                    0d,
                    0,
                    "楼层识别工作线程不可用。"));
            }
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(CreateFailure(
                inputTimestamp,
                0d,
                0d,
                0,
                "楼层识别工作线程正在关闭。"));
        }
        return completion.Task;
    }

    public bool TryRecognizePresence(
        NormalizedRectangle region,
        out string? floor,
        out double confidence,
        MapFloorRecognitionTuning? tuning = null)
    {
        floor = null;
        confidence = 0d;
        if (Volatile.Read(ref _disposeStarted) != 0
            || !_capture.TryCapture(
                region,
                out var frame,
                out _,
                out _))
        {
            _presenceStability.Reset();
            return false;
        }
        var presenceTuning = tuning?.Clone() ?? new MapFloorRecognitionTuning();
        presenceTuning.Normalize();
        var result = _recognizer.Recognize(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride,
            presenceTuning);
        if (!result.Succeeded || result.Floor is not { } observedFloor)
        {
            _presenceStability.Reset();
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = (long)Math.Ceiling(
            Stopwatch.Frequency
            * MapFloorRecognitionRules.ConfirmationSampleIntervalMilliseconds
            / 1000d);
        if (_presenceStability.Observe(
            observedFloor,
            now,
            minimumInterval,
            presenceTuning.FirstFloorConfirmationFrames,
            presenceTuning.SecondFloorConfirmationFrames))
        {
            floor = observedFloor;
            confidence = result.Confidence;
        }
        return true;
    }

    private void WorkLoop()
    {
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(
                        request.CancellationToken);
                    continue;
                }
                if (_shutdownCancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetResult(CreateFailure(
                        request.InputTimestamp,
                        0d,
                        0d,
                        0,
                        "楼层识别工作线程正在关闭。"));
                    continue;
                }
                try
                {
                    request.Completion.TrySetResult(Recognize(request));
                }
                catch (OperationCanceledException)
                    when (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(
                        request.CancellationToken);
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetResult(CreateFailure(
                        request.InputTimestamp,
                        0d,
                        0d,
                        0,
                        $"楼层识别失败：{exception.Message}"));
                }
            }
        }
        finally
        {
            while (_requests.TryTake(out var request))
            {
                request.Completion.TrySetResult(CreateFailure(
                    request.InputTimestamp,
                    0d,
                    0d,
                    0,
                    "楼层识别工作线程已关闭。"));
            }
        }
    }

    private MapFloorRecognitionResult Recognize(Request request)
    {
        var workerStarted = Stopwatch.GetTimestamp();
        var queueMs = GetElapsedMilliseconds(request.EnqueuedTimestamp, workerStarted);
        MapLogCollector.Instance.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info, "开始楼层识别");
        var deadline = request.InputTimestamp
            + (long)Math.Floor(
                Stopwatch.Frequency
                * request.Tuning.MaximumRecognitionWindowMilliseconds
                / 1000d);
        double captureMilliseconds = 0d;
        double analysisMilliseconds = 0d;
        double retryWaitMs = 0d;
        var attempts = 0;
        var stability = new MapFloorStabilityTracker();
        var lastFailure = "楼层按钮在持续识别窗口内未显示稳定状态。";

        while (!request.CancellationToken.IsCancellationRequested
            && !_shutdownCancellation.IsCancellationRequested)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                break;
            attempts++;
            if (!_capture.TryCapture(
                    request.Region,
                    out var frame,
                    out var captureElapsed,
                    out var captureFailure))
            {
                stability.Reset();
                captureMilliseconds += captureElapsed;
                lastFailure = captureFailure;
                if (Stopwatch.GetTimestamp() >= deadline)
                    break;
                var sleepStart = Stopwatch.GetTimestamp();
                Thread.Sleep(2);
                retryWaitMs += GetElapsedMilliseconds(sleepStart, Stopwatch.GetTimestamp());
                continue;
            }
            captureMilliseconds += captureElapsed;
            var classification = _recognizer.Recognize(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride,
                request.Tuning);
            analysisMilliseconds += classification.AnalysisMilliseconds;
            var completedAt = Stopwatch.GetTimestamp();
            var inputToResult = GetElapsedMilliseconds(
                request.InputTimestamp,
                completedAt);
            var workerMs = GetElapsedMilliseconds(workerStarted, completedAt);
            var requestMs = GetElapsedMilliseconds(request.EnqueuedTimestamp, completedAt);
            var workerOverhead = Math.Max(0d,
                workerMs - captureMilliseconds - analysisMilliseconds - retryWaitMs);
            string? stableFloor = null;
            if (classification.Succeeded
                && classification.Floor is { } floor
                && completedAt <= deadline)
            {
                var minimumIntervalTicks = (long)Math.Ceiling(
                    Stopwatch.Frequency
                    * MapFloorRecognitionRules.ConfirmationSampleIntervalMilliseconds
                    / 1000d);
                if (stability.Observe(
                        floor,
                        completedAt,
                        minimumIntervalTicks,
                        request.Tuning.FirstFloorConfirmationFrames,
                        request.Tuning.SecondFloorConfirmationFrames))
                {
                    stableFloor = floor;
                }
            }
            if (stableFloor is { } confirmedFloor)
            {
                MapLogCollector.Instance.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                    $"楼层识别完成：{confirmedFloor.ToUpperInvariant()} · 置信度 {classification.Confidence:P0}",
                    elapsedMs: workerMs,
                    details: new() { ["floor"] = confirmedFloor, ["confidence"] = classification.Confidence });
                MapLogCollector.Instance.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                    $"识别拆解 · 队列{queueMs:F0}ms · Worker{workerMs:F0}ms · 截帧{captureMilliseconds:F0}ms · 匹配{analysisMilliseconds:F0}ms · 重试等待{retryWaitMs:F0}ms · {attempts}次尝试 · 开销{workerOverhead:F0}ms",
                    details: new()
                    {
                        ["queueMs"] = queueMs,
                        ["workerMs"] = workerMs,
                        ["requestMs"] = requestMs,
                        ["inputToResultMs"] = inputToResult,
                        ["captureMs"] = captureMilliseconds,
                        ["analysisMs"] = analysisMilliseconds,
                        ["retryWaitMs"] = retryWaitMs,
                        ["workerOverheadMs"] = workerOverhead,
                        ["attempts"] = attempts
                    });
                return new MapFloorRecognitionResult
                {
                    Succeeded = true,
                    Floor = confirmedFloor,
                    Confidence = classification.Confidence,
                    LocalizationConfidence =
                        classification.LocalizationConfidence,
                    LocalizedRegion =
                        classification.LocalizedRegion?.Clone(),
                    CaptureMilliseconds = captureMilliseconds,
                    AnalysisMilliseconds = analysisMilliseconds,
                    EndToEndMilliseconds = inputToResult,
                    AttemptCount = attempts,
                    QueueMilliseconds = queueMs,
                    WorkerMilliseconds = workerMs,
                    RequestMilliseconds = requestMs,
                    InputToResultMilliseconds = inputToResult,
                    RetryWaitMilliseconds = retryWaitMs,
                    WorkerOverheadMilliseconds = workerOverhead
                };
            }
            if (!classification.Succeeded)
            {
                stability.Reset();
                lastFailure = classification.FailureReason;
            }
            else
            {
                lastFailure = "楼层状态尚未通过连续画面确认。";
            }
            if (completedAt >= deadline)
                break;
            var retryStart = Stopwatch.GetTimestamp();
            Thread.Sleep(2);
            retryWaitMs += GetElapsedMilliseconds(retryStart, Stopwatch.GetTimestamp());
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        if (_shutdownCancellation.IsCancellationRequested)
        {
            lastFailure = "楼层识别工作线程正在关闭。";
        }
        var failureCompletedAt = Stopwatch.GetTimestamp();
        MapLogCollector.Instance.Append(MapLogCategory.FloorRecognition, MapLogLevel.Warning,
            $"楼层识别失败：{lastFailure}",
            elapsedMs: GetElapsedMilliseconds(request.InputTimestamp, failureCompletedAt));
        return CreateFailure(
            request.InputTimestamp,
            request.EnqueuedTimestamp,
            workerStarted,
            captureMilliseconds,
            analysisMilliseconds,
            retryWaitMs,
            attempts,
            lastFailure);
    }

    private static MapFloorRecognitionResult CreateFailure(
        long inputTimestamp,
        double captureMilliseconds,
        double analysisMilliseconds,
        int attempts,
        string reason) =>
        CreateFailure(
            inputTimestamp,
            inputTimestamp,
            inputTimestamp,
            captureMilliseconds,
            analysisMilliseconds,
            0d,
            attempts,
            reason);

    private static MapFloorRecognitionResult CreateFailure(
        long inputTimestamp,
        long enqueuedTimestamp,
        long workerStartedTimestamp,
        double captureMilliseconds,
        double analysisMilliseconds,
        double retryWaitMs,
        int attempts,
        string reason) =>
        new()
        {
            CaptureMilliseconds = captureMilliseconds,
            AnalysisMilliseconds = analysisMilliseconds,
            EndToEndMilliseconds = GetElapsedMilliseconds(
                inputTimestamp,
                Stopwatch.GetTimestamp()),
            AttemptCount = attempts,
            FailureReason = reason,
            QueueMilliseconds = GetElapsedMilliseconds(enqueuedTimestamp, workerStartedTimestamp),
            WorkerMilliseconds = GetElapsedMilliseconds(
                workerStartedTimestamp,
                Stopwatch.GetTimestamp()),
            RequestMilliseconds = GetElapsedMilliseconds(
                enqueuedTimestamp,
                Stopwatch.GetTimestamp()),
            InputToResultMilliseconds = GetElapsedMilliseconds(
                inputTimestamp,
                Stopwatch.GetTimestamp()),
            RetryWaitMilliseconds = retryWaitMs,
            WorkerOverheadMilliseconds = Math.Max(0d,
                GetElapsedMilliseconds(workerStartedTimestamp, Stopwatch.GetTimestamp())
                - captureMilliseconds - analysisMilliseconds - retryWaitMs)
        };

    private static double GetElapsedMilliseconds(long start, long end) =>
        Math.Max(0d, (end - start) * 1000d / Stopwatch.Frequency);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        _shutdownCancellation.Cancel();
        _requests.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            ReleaseResources();
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ =>
            {
                _thread.Join();
                ReleaseResources();
            });
    }

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
            return;
        _recognizer.Dispose();
        _capture.Dispose();
        _requests.Dispose();
        _shutdownCancellation.Dispose();
    }

    private sealed record Request(
        NormalizedRectangle Region,
        long InputTimestamp,
        long EnqueuedTimestamp,
        CancellationToken CancellationToken,
        MapFloorRecognitionTuning Tuning,
        TaskCompletionSource<MapFloorRecognitionResult> Completion);
}
