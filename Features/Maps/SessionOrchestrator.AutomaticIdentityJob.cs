using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private AutomaticIdentityJob? _automaticIdentityJob;
    private Task _automaticIdentityWorker = Task.CompletedTask;
    private long _automaticIdentityInvalidation;

    private sealed class AutomaticIdentityJob : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly object _revisionGate = new();
        private MapCatalogRevision _revision;
        private bool _started;
        private int _disposed;
        public MapMatchSnapshot Match { get; }
        public bool CanUseCatalog(MapCatalogRevision revision)
        {
            // A queued job binds the catalog only after its cache predecessor
            // completes. Refresh clears all older jobs under the owner lock.
            lock (_revisionGate)
                return !_started || _revision == revision;
        }
        public string? Floor { get; }
        public CapturedGameFrame OriginFrame { get; }
        public long Generation { get; }
        public int IndependentCornerCount { get; }
        public MapViewportColorSignature? ReadinessSignature { get; private set; }
        public Task<MapAutomaticIdentityAttempt> Task { get; }

        public AutomaticIdentityJob(MapCvRecognitionService recognition,
            CapturedGameFrame source, MapMatchSnapshot match, string? floor,
            MapRecognitionTuning tuning, MapStructureRegistrationTuning validation,
            CancellationToken matchCancellation, long generation, int recognitionBudgetMilliseconds,
            int independentCornerCount, Task precedingWorker)
        {
            Generation = generation;
            IndependentCornerCount = independentCornerCount;
            Match = match;
            _revision = recognition.CatalogRevision;
            Floor = floor;
            OriginFrame = source;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(matchCancellation);
            var frozen = new CapturedGameFrame(source.Image.Clone(), source.ClientBounds,
                source.ViewportBounds, source.WindowHandle)
            {
                CaptureBackend = source.CaptureBackend,
                CaptureSystemRelativeTicks = source.CaptureSystemRelativeTicks,
                DetectedFloorKey = source.DetectedFloorKey,
                UiExclusionRegions = source.UiExclusionRegions
            };
            // Always enter the delegate so the owned frame is disposed even
            // when a match ends before this worker is scheduled.
            Task = System.Threading.Tasks.Task.Run(async () =>
            {
                using (frozen)
                {
                    // Cancellation releases the open-map consumer, not native
                    // recognition ownership. A replacement starts only after
                    // the preceding worker has actually left the recognizer.
                    await ObserveAutomaticIdentityWorkerAsync(precedingWorker);
                    _cancellation.Token.ThrowIfCancellationRequested();
                    lock (_revisionGate)
                    {
                        _revision = recognition.CatalogRevision;
                        _started = true;
                    }
                    // Retained identity work survives closing the map, but
                    // still obeys the configured recognition time window.
                    // The inherited open-map scope may already be cancelled
                    // before a queued worker starts. This job belongs to the
                    // match, so closing the map cannot zero its own budget.
                    var budget = recognitionBudgetMilliseconds;
                    var timer = Stopwatch.StartNew();
                    MapLogCollector.Instance.Append(MapLogCategory.Session, MapLogLevel.Info,
                        "自动身份计算开始", details: new()
                        {
                            ["matchVersion"] = match.Version, ["generation"] = generation,
                            ["floor"] = floor, ["captureTicks"] = frozen.CaptureSystemRelativeTicks,
                            ["viewport"] = frozen.ViewportBounds,
                            ["independentCornerCount"] = independentCornerCount
                        });
                    using var scope = MapNoDoorAlignmentBudgetContext.Enter(() =>
                        budget - (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds));
                    MapAutomaticIdentityAttempt attempt;
                    try
                    {
                        attempt = recognition.RecognizeAutomaticIdentity(frozen,
                            match.MapClass, floor, tuning, validation, _cancellation.Token);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        MapLogCollector.Instance.Append(MapLogCategory.Session, MapLogLevel.Info,
                            "自动身份计算已取消", elapsedMs: timer.Elapsed.TotalMilliseconds,
                            details: new() { ["matchVersion"] = match.Version,
                                ["generation"] = generation, ["floor"] = floor,
                                ["captureTicks"] = frozen.CaptureSystemRelativeTicks });
                        throw;
                    }
                    var readinessSignatureMilliseconds = 0d;
                    if (attempt.Accepted)
                    {
                        var signatureTimer = Stopwatch.StartNew();
                        try
                        {
                            // Derive this optional reopen hint from the job's
                            // owned frozen image before disposing it. OriginFrame
                            // belongs to its caller and may already be disposed.
                            ReadinessSignature = MapViewportPresenceDetector
                                .CreateSignature(frozen.Image);
                        }
                        catch (Exception)
                        {
                            // Keep accepted identity authoritative; an absent
                            // hint simply retains the established stable path.
                        }
                        finally
                        {
                            signatureTimer.Stop();
                            readinessSignatureMilliseconds =
                                signatureTimer.Elapsed.TotalMilliseconds;
                        }
                    }
                    // Record match-owned computation even when closing the
                    // map has already revoked its caller's publication scope.
                    MapLogCollector.Instance.Append(MapLogCategory.Session, MapLogLevel.Info,
                        "自动身份计算结束", elapsedMs: timer.Elapsed.TotalMilliseconds,
                        details: new()
                        {
                            ["status"] = attempt.Status.ToString(),
                            ["candidateCount"] = attempt.CandidateCount,
                            ["comparedCount"] = attempt.ComparedCount,
                            ["nativeAttemptedCount"] = attempt.CandidateDiagnostics.Count(
                                candidate => candidate.VpsgAttempted),
                            ["geometryComparedCount"] = attempt.CandidateDiagnostics.Count(
                                candidate => candidate.GeometryComparisonComplete),
                            ["failureReason"] = attempt.FailureReason,
                            ["readinessSignatureAvailable"] = ReadinessSignature is not null,
                            ["readinessSignatureMs"] = readinessSignatureMilliseconds,
                            ["matchVersion"] = match.Version,
                            ["generation"] = generation,
                            ["floor"] = floor,
                            ["captureTicks"] = frozen.CaptureSystemRelativeTicks,
                            ["viewport"] = frozen.ViewportBounds
                        });
                    return attempt;
                }
            });
            ObserveAutomaticIdentityWorkerFault(Task);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cancellation.Cancel();
            _ = Task.ContinueWith(_ => _cancellation.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private AutomaticIdentityJob GetOrStartAutomaticIdentityJob(
        CapturedGameFrame frame, MapMatchSnapshot match, string? floor,
        MapRecognitionTuning tuning, MapStructureRegistrationTuning validation,
        bool requireCurrentFrame = false)
    {
        // Six spatially independent corners are the existing minimum geometric
        // witness. Here their count only chooses which frame gets compute time;
        // it never changes identity acceptance or removes a map from the pool.
        using var observation = Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds,
            excludedScreenRegions: frame.UiExclusionRegions);
        var corners = MapLocalCornerGeometryExtractor.ExtractCorners(
            observation.ProposalEdges, observation.ProposalEdges);
        var independent = new List<OpenCvSharp.Point2d>();
        foreach (var corner in corners)
            if (independent.All(point => double.Hypot(point.X - corner.Point.X,
                point.Y - corner.Point.Y) >= 6))
                independent.Add(corner.Point);

        lock (_mapOpenCancellationOwner.SyncRoot)
        {
            if (!requireCurrentFrame && _automaticIdentityJob is { } existing
                && existing.Match == match
                && existing.CanUseCatalog(_recognition.CatalogRevision)
                && string.Equals(existing.Floor, floor, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Task.IsCompletedSuccessfully && existing.Task.Result.Accepted)
                {
                    LogAutomaticIdentityReuse(existing, frame, independent.Count);
                    return existing;
                }
                if (!existing.Task.IsCompleted)
                {
                    if (independent.Count < existing.IndependentCornerCount + 6)
                    {
                        LogAutomaticIdentityReuse(existing, frame, independent.Count);
                        return existing;
                    }
                    _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
                        "自动身份计算切换到新增结构画面", details: new()
                        {
                            ["oldIndependentCornerCount"] = existing.IndependentCornerCount,
                            ["newIndependentCornerCount"] = independent.Count,
                            ["oldGeneration"] = existing.Generation,
                            ["captureTicks"] = frame.CaptureSystemRelativeTicks
                        });
                }
            }

            ClearAutomaticIdentityJob();
            var job = new AutomaticIdentityJob(_recognition,
                frame, match, floor, tuning, validation, CurrentMatchCancellationToken,
                Volatile.Read(ref _automaticIdentityInvalidation),
                _settings!.SessionTuning.OpeningTimeoutMilliseconds,
                independent.Count, _automaticIdentityWorker);
            _automaticIdentityWorker = job.Task;
            return _automaticIdentityJob = job;
        }
    }

    private void LogAutomaticIdentityReuse(AutomaticIdentityJob job,
        CapturedGameFrame frame, int independentCornerCount)
    {
        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
            "自动身份计算复用进度", details: new()
            {
                ["matchVersion"] = job.Match.Version, ["generation"] = job.Generation,
                ["completed"] = job.Task.IsCompleted, ["floor"] = job.Floor,
                ["originalIndependentCornerCount"] = job.IndependentCornerCount,
                ["currentIndependentCornerCount"] = independentCornerCount,
                ["captureTicks"] = frame.CaptureSystemRelativeTicks,
                ["viewport"] = frame.ViewportBounds
            });
    }

    private static async Task ObserveAutomaticIdentityWorkerAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        // Every producer installs a completion observer, so fault diagnostics
        // do not depend on a later open and are not duplicated by each drain.
        catch (Exception) { }
    }

    private static void ObserveAutomaticIdentityWorkerFault(Task worker)
    {
        _ = worker.ContinueWith(completed =>
            MapLogCollector.Instance.Append(MapLogCategory.Session, MapLogLevel.Error,
                "自动识别资源任务异常", details: new()
                { ["exception"] = completed.Exception!.ToString() }),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private Task DrainAutomaticIdentityWorkerAsync(CancellationToken token = default)
    {
        Task worker;
        lock (_mapOpenCancellationOwner.SyncRoot)
            worker = _automaticIdentityWorker;
        return ObserveAutomaticIdentityWorkerAsync(worker).WaitAsync(token);
    }

    private void ClearAutomaticIdentityJob(bool invalidateOperation = true)
    {
        lock (_mapOpenCancellationOwner.SyncRoot)
        {
            if (invalidateOperation)
                Interlocked.Increment(ref _automaticIdentityInvalidation);
            var job = Interlocked.Exchange(ref _automaticIdentityJob, null);
            job?.Dispose();
        }
    }

    private async Task<bool> TryAcquireExplicitRecognitionGateAsync(CancellationToken token)
    {
        bool interruptsMapOpen;
        interruptsMapOpen = _mapOpenCancellationOwner.HasOwner;
        // Preserve an explicit scan request while revoking both pending
        // identity and current-frame pose publication by the map-open owner.
        if (interruptsMapOpen)
            CancelMapOpenAlignment();
        bool acquired;
        if (interruptsMapOpen)
        {
            await _scanGate.WaitAsync(token);
            acquired = true;
        }
        else
            acquired = await _scanGate.WaitAsync(0, token);
        if (!acquired)
            return false;
        try
        {
            ClearAutomaticIdentityJob();
            await DrainAutomaticIdentityWorkerAsync(token);
            return true;
        }
        catch
        {
            _scanGate.Release();
            throw;
        }
    }

}
