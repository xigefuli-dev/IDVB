using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using OpenCvSharp;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    private const int SixFitBatchSize = 64;
    private const int SixFitMinimumParallelGroups = 4;
    private const int SixFitMaximumWorkers = 12;

    private sealed record SixCornerFitWorkItem(int OriginalIndex, string Key,
        List<(int Source, int Query, double Error)> Matches, CornerScaleInterval Range);

    private sealed record SixCornerFitWorkResult(SixCornerFitWorkItem Item, bool Started,
        IReadOnlyList<CornerFitPose>? Fits, bool Uncertified, Exception? Exception,
        double ElapsedMilliseconds, SixCornerFitDiagnostics.MeasurementSnapshot Measurements,
        IReadOnlyList<SixCornerFitMeasuredPose> MeasuredPoseJournal,
        SixCornerFitCountMetrics CountMemoMetrics);

    private readonly record struct SixCornerFitMeasuredPose(
        (double Scale, double X, double Y) Key, (int Count, double Error) Value);

    private readonly record struct SixCornerFitCountMetrics(
        int LogicalRequests, int CountMatchesCalls, int CountMatchesCompleted,
        int FactoryInvocations, int StableMemoHits, int BatchMemoHits,
        int FactoryCompetitionLosers, int NonFiniteUncachedRequests);

    private sealed class SixCornerFitCountValue
    {
        internal SixCornerFitCountValue(int count, double error)
        {
            Count = count;
            Error = error;
        }

        internal int Count { get; }
        internal double Error { get; }
    }

    // One resolver belongs to one fitted correspondence group. Workers only
    // read the stable memo; all cross-worker sharing is limited to this batch.
    private sealed class SixCornerFitCountMemo
    {
        private readonly Dictionary<(double Scale, double X, double Y), (int Count, double Error)>? _stable;
        private readonly ConcurrentDictionary<(double Scale, double X, double Y), SixCornerFitCountValue> _batch;
        private readonly IReadOnlyList<MapLocalCorner> _source;
        private readonly IReadOnlyList<MapLocalCorner> _query;
        private readonly Dictionary<(int X, int Y), int[]> _queryCells;
        private readonly CancellationToken _cancellationToken;
        private readonly SixCornerFitDiagnostics? _diagnostics;
        private readonly List<SixCornerFitMeasuredPose> _journal = [];
        private int _logicalRequests, _countMatchesCalls, _countMatchesCompleted;
        private int _factoryInvocations, _stableMemoHits, _batchMemoHits;
        private int _factoryCompetitionLosers, _nonFiniteUncachedRequests;

        internal SixCornerFitCountMemo(
            Dictionary<(double Scale, double X, double Y), (int Count, double Error)>? stable,
            ConcurrentDictionary<(double Scale, double X, double Y), SixCornerFitCountValue> batch,
            IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
            Dictionary<(int X, int Y), int[]> queryCells, CancellationToken cancellationToken,
            SixCornerFitDiagnostics? diagnostics)
        {
            _stable = stable;
            _batch = batch;
            _source = source;
            _query = query;
            _queryCells = queryCells;
            _cancellationToken = cancellationToken;
            _diagnostics = diagnostics;
        }

        internal (int Count, double Error) Resolve(double scale, Point2d translation)
        {
            _logicalRequests++;
            var key = (Scale: scale, X: translation.X, Y: translation.Y);
            if (!double.IsFinite(scale) || !double.IsFinite(translation.X)
                || !double.IsFinite(translation.Y))
            {
                _nonFiniteUncachedRequests++;
                _countMatchesCalls++;
                var raw = Measure(key);
                _countMatchesCompleted++;
                return raw;
            }

            if (_stable is not null && _stable.TryGetValue(key, out var stableValue))
            {
                _stableMemoHits++;
                _journal.Add(new(key, stableValue));
                return stableValue;
            }
            if (_batch.TryGetValue(key, out var batchValue))
            {
                _batchMemoHits++;
                var reused = (batchValue.Count, batchValue.Error);
                _journal.Add(new(key, reused));
                return reused;
            }

            SixCornerFitCountValue? created = null;
            var factoryInvoked = false;
            var resolved = _batch.GetOrAdd(key, _ =>
            {
                factoryInvoked = true;
                _factoryInvocations++;
                _countMatchesCalls++;
                var exact = Measure(key);
                _countMatchesCompleted++;
                // Do not publish a completed count after this worker's solve
                // budget has expired. Other groups can only reuse published values.
                CheckGeometryBudget(_cancellationToken);
                var value = new SixCornerFitCountValue(exact.Count, exact.Error);
                created = value;
                return value;
            });
            if (!factoryInvoked) _batchMemoHits++;
            else if (!ReferenceEquals(created, resolved)) _factoryCompetitionLosers++;
            var value = (resolved.Count, resolved.Error);
            _journal.Add(new(key, value));
            return value;
        }

        private (int Count, double Error) Measure(
            (double Scale, double X, double Y) key) => _diagnostics is null
                ? CountMatches(_source, _query, _queryCells, key.Scale,
                    new(key.X, key.Y))
                : _diagnostics.MeasureVerification(_source, _query, _queryCells,
                    key.Scale, new(key.X, key.Y));

        internal IReadOnlyList<SixCornerFitMeasuredPose> SnapshotJournal() => _journal.ToArray();

        internal SixCornerFitCountMetrics SnapshotMetrics() => new(_logicalRequests,
            _countMatchesCalls, _countMatchesCompleted, _factoryInvocations,
            _stableMemoHits, _batchMemoHits, _factoryCompetitionLosers,
            _nonFiniteUncachedRequests);
    }

    private sealed record SixCornerFitBatchResult(IReadOnlyList<SixCornerFitWorkResult> Results,
        bool UsedParallel, int WorkerCount, string? SerialFallbackReason,
        double WallElapsedMilliseconds);

    private sealed record SixCornerFitPending(int OriginalIndex, string Key,
        List<(int Source, int Query, double Error)> Matches, CornerScaleInterval Range,
        IReadOnlyList<CornerFitPose>? CachedFits);

    private static void CollectSixCornerFitFallback(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        IReadOnlyList<(int Source, int Query, double Error)> tentative,
        CornerFitMemo memo, List<CornerFitPose> collected,
        CancellationToken cancellationToken, SixCornerFitDiagnostics? diagnostics)
    {
        using var matches = EnumerateSixCornerMatches(source, query, tentative,
            cancellationToken).GetEnumerator();
        var originalIndex = 0;
        while (true)
        {
            var pending = new List<SixCornerFitPending>(SixFitBatchSize);
            var yieldedUnbufferedGroup = false;
            try
            {
                while (pending.Count < SixFitBatchSize)
                {
                    if (!matches.MoveNext()) break;
                    yieldedUnbufferedGroup = true;
                    CheckGeometryBudget(cancellationToken);
                    var (groupMatches, interval) = matches.Current;
                    var key = CornerCorrespondenceKey(groupMatches);
                    var cached = memo.SixMatches.TryGetValue(key, out var fitted);
                    pending.Add(new(originalIndex++, key, groupMatches, interval,
                        cached ? fitted : null));
                    yieldedUnbufferedGroup = false;
                }
            }
            catch
            {
                diagnostics?.RecordUncommittedGroups(
                    pending.Count + (yieldedUnbufferedGroup ? 1 : 0), []);
                throw;
            }
            if (pending.Count == 0) return;

            var workItems = pending.Where(item => item.CachedFits is null)
                .Select(item => new SixCornerFitWorkItem(item.OriginalIndex, item.Key,
                    item.Matches, item.Range)).ToArray();
            var batch = ComputeSixCornerFitBatch(source, query, queryCells, workItems,
                cancellationToken, diagnostics, memo.MeasuredPoses);
            diagnostics?.RecordBatch(pending.Count, batch);
            var results = batch.Results.ToDictionary(result => result.Item.OriginalIndex);

            for (var index = 0; index < pending.Count; index++)
            {
                var item = pending[index];
                try
                {
                    CheckGeometryBudget(cancellationToken);
                }
                catch
                {
                    diagnostics?.RecordUncommittedGroups(pending.Count - index,
                        batch.Results.Where(result => result.Item.OriginalIndex >= item.OriginalIndex));
                    throw;
                }

                var cached = item.CachedFits is not null;
                diagnostics?.BeginGroup(item.Key, item.Range, cached);
                IReadOnlyList<CornerFitPose> fitted;
                if (cached)
                {
                    fitted = item.CachedFits!;
                }
                else
                {
                    var result = results[item.OriginalIndex];
                    diagnostics?.RecordCommittedFit(result.ElapsedMilliseconds,
                        result.Measurements);
                    if (result.Exception is not null)
                    {
                        diagnostics?.RecordFailedFit();
                        diagnostics?.RecordFailedCountMemo(result.CountMemoMetrics);
                        diagnostics?.RecordUncommittedGroups(pending.Count - index - 1,
                            batch.Results.Where(other => other.Item.OriginalIndex > item.OriginalIndex));
                        ExceptionDispatchInfo.Capture(result.Exception).Throw();
                    }
                    if (!result.Started || result.Fits is null)
                        throw new InvalidOperationException("A queued six-corner fit was not started.");
                    fitted = result.Fits;
                    memo.HasUncertifiedFits |= result.Uncertified;
                    // Never memoize an interrupted or partially evaluated set.
                    memo.SixMatches.Add(item.Key, fitted);
                    foreach (var measured in result.MeasuredPoseJournal)
                        memo.MeasuredPoses.TryAdd(measured.Key, measured.Value);
                    diagnostics?.RecordCommittedCountMemo(result.CountMemoMetrics);
                    diagnostics?.CompleteFit(fitted.Count, result.Uncertified);
                }
                diagnostics?.ObserveGeneratingGateMasks(fitted);
                collected.AddRange(fitted);
            }
        }
    }

    private static SixCornerFitBatchResult ComputeSixCornerFitBatch(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        IReadOnlyList<SixCornerFitWorkItem> uncachedGroups,
        CancellationToken cancellationToken, SixCornerFitDiagnostics? diagnostics,
        Dictionary<(double Scale, double X, double Y), (int Count, double Error)>? stableMeasuredPoses = null)
    {
        var started = Stopwatch.GetTimestamp();
        var results = new SixCornerFitWorkResult[uncachedGroups.Count];
        for (var index = 0; index < uncachedGroups.Count; index++)
            results[index] = new(uncachedGroups[index], false, null, false, null, 0, default,
                [], default);
        if (uncachedGroups.Count == 0)
            return new(results, false, 0, "no-uncached-groups",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var sharedCounts = new ConcurrentDictionary<
            (double Scale, double X, double Y), SixCornerFitCountValue>();

        SixCornerFitWorkResult FitOne(SixCornerFitWorkItem item)
        {
            var workerDiagnostics = diagnostics?.CreateGroupObserver();
            var countMemo = new SixCornerFitCountMemo(stableMeasuredPoses, sharedCounts,
                source, query, queryCells, cancellationToken, workerDiagnostics);
            var fitStarted = Stopwatch.GetTimestamp();
            IReadOnlyList<CornerFitPose>? fits = null;
            var uncertified = false;
            Exception? exception = null;
            try
            {
                fits = FitSixCornerMatchesCore(source, query, queryCells, item.Matches,
                    item.Range, cancellationToken, out uncertified, workerDiagnostics, countMemo);
            }
            catch (Exception error)
            {
                exception = error;
            }
            return new(item, true, fits, uncertified, exception,
                Stopwatch.GetElapsedTime(fitStarted).TotalMilliseconds,
                workerDiagnostics?.SnapshotMeasurements() ?? default,
                countMemo.SnapshotJournal(), countMemo.SnapshotMetrics());
        }

        var flowSuppressed = ExecutionContext.IsFlowSuppressed();
        // Leave processor capacity for capture, input and rendering. Group order,
        // fit arithmetic, shared budget and the closing join stay unchanged.
        var availableWorkers = Math.Min(SixFitMaximumWorkers,
            Math.Max(1, Environment.ProcessorCount - 2));
        var usedParallel = uncachedGroups.Count >= SixFitMinimumParallelGroups
            && availableWorkers > 1 && !flowSuppressed;
        var serialFallbackReason = usedParallel ? null
            : uncachedGroups.Count < SixFitMinimumParallelGroups ? "small-batch"
            : availableWorkers <= 1 ? "limited-processor-count"
            : flowSuppressed ? "execution-context-flow-suppressed" : null;
        var workerCount = usedParallel
            ? Math.Min(availableWorkers, uncachedGroups.Count) : 1;
        if (!usedParallel)
        {
            for (var index = 0; index < uncachedGroups.Count; index++)
            {
                results[index] = FitOne(uncachedGroups[index]);
                if (results[index].Exception is not null) break;
            }
        }
        else
        {
            var next = -1;
            var workers = new Task[workerCount];
            for (var worker = 0; worker < workerCount; worker++)
            {
                workers[worker] = Task.Run(() =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next);
                        if (index >= uncachedGroups.Count) return;
                        results[index] = FitOne(uncachedGroups[index]);
                    }
                });
            }
            Task.WhenAll(workers).GetAwaiter().GetResult();
        }

        return new(results, usedParallel, workerCount, serialFallbackReason,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
