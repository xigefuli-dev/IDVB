using System.Diagnostics;
using OpenCvSharp;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    private static bool IsGeneratingGateCompatible(Point2d anchorCenter, double anchorRadius,
        IReadOnlyList<Point2d> liveGates, double scale, double x, double y) =>
        liveGates.Any(gate => Distance(gate, anchorCenter * scale + new Point2d(x, y))
            <= Math.Max(8, anchorRadius * scale / 6));

    // Opt-in measurement only. Capture an interrupted fit's actual input rather
    // than reconstructing it later from a different extraction or reference.
    private sealed class SixCornerFitDiagnostics
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _groups, _memoHits, _newFits, _completedFits, _poses, _uncertified;
        private int _circleCalls, _verificationCalls, _verificationFailures;
        private double _circleMilliseconds, _verificationMilliseconds;
        private int _batchCount, _parallelBatchCount, _serialBatchCount, _maximumWorkers;
        private int _smallBatchSerialCount, _flowSuppressedSerialCount, _emptyFitBatchCount;
        private int _bufferedGroups, _queuedFitGroups, _computedFitGroups;
        private int _committedFitAttempts, _failedFitGroups;
        private int _uncommittedGroups, _uncommittedComputedFitGroups;
        private int _uncommittedCompletedFitGroups, _uncommittedFailedFitGroups;
        private int _uncommittedNotStartedFitGroups;
        private int _uncommittedCircleCalls, _uncommittedVerificationCalls;
        private double _fitInvocationMillisecondsSum, _uncommittedFitInvocationMillisecondsSum;
        private double _batchFitWallMillisecondsSum;
        private double _uncommittedCircleMilliseconds, _uncommittedVerificationMilliseconds;
        private SixCornerFitCountMetrics _committedCountMemo;
        private SixCornerFitCountMetrics _uncommittedCountMemo;
        private bool _enumerationComplete;
        private string? _unfinishedKey;
        private CornerScaleInterval? _unfinishedRange;
        private readonly GeneratingGateDiagnosticContext? _gateContext;
        private readonly Dictionary<(int FitCount, ulong PassMask), long> _gateMaskHistogram = [];
        private long _observedGateGroups, _unscoredGateGroups, _groupsAnyGatePass;
        private long _groupsAllGateFail, _groupsWithoutFitPoses, _gateCandidatesPassed, _gateCandidatesFailed;
        private long _gateMasksOver64Candidates;

        private SixCornerFitDiagnostics(GeneratingGateDiagnosticContext? gateContext) =>
            _gateContext = gateContext;

        internal static SixCornerFitDiagnostics? Start(GeneratingGateDiagnosticContext? gateContext)
        {
            try { return MapLogCollector.Instance.IsEnabled ? new(gateContext) : null; }
            catch { return null; }
        }

        internal void BeginGroup(string key, CornerScaleInterval range, bool cached)
        {
            try
            {
                _groups++;
                if (cached) { _memoHits++; return; }
                _newFits++;
                _unfinishedKey = key;
                _unfinishedRange = range;
            }
            catch { }
        }

        internal void CompleteFit(int poses, bool uncertified)
        {
            try
            {
                _completedFits++;
                _poses += poses;
                if (uncertified) _uncertified++;
                _unfinishedKey = null;
                _unfinishedRange = null;
            }
            catch { }
        }

        internal readonly record struct MeasurementSnapshot(int CircleCalls,
            double CircleMilliseconds, int VerificationCalls, int VerificationFailures,
            double VerificationMilliseconds);

        internal SixCornerFitDiagnostics CreateGroupObserver() => new(_gateContext);

        internal MeasurementSnapshot SnapshotMeasurements() => new(_circleCalls,
            _circleMilliseconds, _verificationCalls, _verificationFailures,
            _verificationMilliseconds);

        internal void RecordBatch(int bufferedGroups, SixCornerFitBatchResult batch)
        {
            try
            {
                _batchCount++;
                _bufferedGroups += bufferedGroups;
                if (batch.UsedParallel) _parallelBatchCount++;
                else _serialBatchCount++;
                if (batch.SerialFallbackReason == "small-batch") _smallBatchSerialCount++;
                else if (batch.SerialFallbackReason == "execution-context-flow-suppressed")
                    _flowSuppressedSerialCount++;
                else if (batch.SerialFallbackReason == "no-uncached-groups") _emptyFitBatchCount++;
                _maximumWorkers = Math.Max(_maximumWorkers, batch.WorkerCount);
                _queuedFitGroups += batch.Results.Count;
                _computedFitGroups += batch.Results.Count(result => result.Started);
                _batchFitWallMillisecondsSum += batch.WallElapsedMilliseconds;
            }
            catch { }
        }

        internal void RecordCommittedFit(double elapsedMilliseconds,
            MeasurementSnapshot measurements)
        {
            try
            {
                _committedFitAttempts++;
                _fitInvocationMillisecondsSum += elapsedMilliseconds;
                MergeMeasurements(measurements);
            }
            catch { }
        }

        internal void RecordFailedFit()
        {
            try { _failedFitGroups++; }
            catch { }
        }

        internal void RecordCommittedCountMemo(SixCornerFitCountMetrics measurements)
        {
            try { _committedCountMemo = AddCountMemoMetrics(_committedCountMemo, measurements); }
            catch { }
        }

        internal void RecordFailedCountMemo(SixCornerFitCountMetrics measurements)
        {
            try { _uncommittedCountMemo = AddCountMemoMetrics(_uncommittedCountMemo, measurements); }
            catch { }
        }

        internal void RecordUncommittedGroups(int groups,
            IEnumerable<SixCornerFitWorkResult> results)
        {
            try
            {
                _uncommittedGroups += groups;
                foreach (var result in results)
                {
                    if (!result.Started)
                    {
                        _uncommittedNotStartedFitGroups++;
                        continue;
                    }
                    _uncommittedCountMemo = AddCountMemoMetrics(
                        _uncommittedCountMemo, result.CountMemoMetrics);
                    _uncommittedComputedFitGroups++;
                    if (result.Exception is null && result.Fits is not null)
                        _uncommittedCompletedFitGroups++;
                    if (result.Exception is not null) _uncommittedFailedFitGroups++;
                    _uncommittedFitInvocationMillisecondsSum += result.ElapsedMilliseconds;
                    _uncommittedCircleCalls += result.Measurements.CircleCalls;
                    _uncommittedCircleMilliseconds += result.Measurements.CircleMilliseconds;
                    _uncommittedVerificationCalls += result.Measurements.VerificationCalls;
                    _uncommittedVerificationMilliseconds += result.Measurements.VerificationMilliseconds;
                }
            }
            catch { }
        }

        private static SixCornerFitCountMetrics AddCountMemoMetrics(
            SixCornerFitCountMetrics left, SixCornerFitCountMetrics right) => new(
            left.LogicalRequests + right.LogicalRequests,
            left.CountMatchesCalls + right.CountMatchesCalls,
            left.CountMatchesCompleted + right.CountMatchesCompleted,
            left.FactoryInvocations + right.FactoryInvocations,
            left.StableMemoHits + right.StableMemoHits,
            left.BatchMemoHits + right.BatchMemoHits,
            left.FactoryCompetitionLosers + right.FactoryCompetitionLosers,
            left.NonFiniteUncachedRequests + right.NonFiniteUncachedRequests);

        private static object CountMemoDiagnostic(SixCornerFitCountMetrics metrics) => new
        {
            logicalRequests = metrics.LogicalRequests,
            countMatchesCalls = metrics.CountMatchesCalls,
            countMatchesCompleted = metrics.CountMatchesCompleted,
            factoryInvocations = metrics.FactoryInvocations,
            stableMemoHits = metrics.StableMemoHits,
            batchMemoHits = metrics.BatchMemoHits,
            factoryCompetitionLosers = metrics.FactoryCompetitionLosers,
            nonFiniteUncachedRequests = metrics.NonFiniteUncachedRequests
        };

        private void MergeMeasurements(MeasurementSnapshot measurements)
        {
            _circleCalls += measurements.CircleCalls;
            _circleMilliseconds += measurements.CircleMilliseconds;
            _verificationCalls += measurements.VerificationCalls;
            _verificationFailures += measurements.VerificationFailures;
            _verificationMilliseconds += measurements.VerificationMilliseconds;
        }

        internal void ObserveGeneratingGateMasks(IReadOnlyList<CornerFitPose> fits)
        {
            try
            {
                if (_gateContext is null)
                {
                    _unscoredGateGroups++;
                    return;
                }
                _observedGateGroups++;
                ulong mask = 0;
                var anyPass = false;
                for (var index = 0; index < fits.Count; index++)
                {
                    var fit = fits[index];
                    var passed = IsGeneratingGateCompatible(_gateContext.AnchorCenter,
                        _gateContext.AnchorRadius, _gateContext.LiveGates,
                        fit.Scale, fit.X, fit.Y);
                    if (passed)
                    {
                        anyPass = true;
                        _gateCandidatesPassed++;
                        if (index < 64) mask |= 1UL << index;
                    }
                    else _gateCandidatesFailed++;
                }
                if (fits.Count > 64) _gateMasksOver64Candidates++;
                var key = (fits.Count, mask);
                _gateMaskHistogram[key] = _gateMaskHistogram.TryGetValue(key, out var count) ? count + 1 : 1;
                if (fits.Count == 0) _groupsWithoutFitPoses++;
                else if (anyPass) _groupsAnyGatePass++;
                else _groupsAllGateFail++;
            }
            catch { }
        }

        internal void CompleteEnumeration()
        {
            try { _enumerationComplete = true; }
            catch { }
        }

        internal (Point2d Center, double Radius) MeasureCircle(
            IReadOnlyList<Point2d> centers, CancellationToken cancellationToken)
        {
            long started = 0;
            var hasStart = false;
            try { started = Stopwatch.GetTimestamp(); hasStart = true; }
            catch { }
            try { return MinimumCornerEnclosingCircle(centers, cancellationToken); }
            finally
            {
                try
                {
                    if (hasStart)
                    {
                        _circleCalls++;
                        _circleMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                }
                catch { }
            }
        }

        internal (int Count, double Error) MeasureVerification(
            IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
            Dictionary<(int X, int Y), int[]> queryCells, double scale, Point2d translation)
        {
            long started = 0;
            var hasStart = false;
            try { started = Stopwatch.GetTimestamp(); hasStart = true; }
            catch { }
            try
            {
                var result = CountMatches(source, query, queryCells, scale, translation);
                try { if (result.Count < 6) _verificationFailures++; }
                catch { }
                return result;
            }
            finally
            {
                try
                {
                    if (hasStart)
                    {
                        _verificationCalls++;
                        _verificationMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                }
                catch { }
            }
        }

        internal void Write(IReadOnlyList<MapLocalCorner> source,
            IReadOnlyList<MapLocalCorner> query,
            IReadOnlyList<(int Source, int Query, double Error)> tentative,
            int sourceA, int queryA, int sourceB, int queryB,
            double scale, Point2d translation)
        {
            try
            {
                var details = new Dictionary<string, object?>
                {
                    ["sourceCount"] = source.Count,
                    ["queryCount"] = query.Count,
                    ["tentativeCount"] = tentative.Count,
                    ["visitedSixGroups"] = _groups,
                    ["memoHits"] = _memoHits,
                    ["newSixFitCalls"] = _newFits,
                    ["completedSixFitCalls"] = _completedFits,
                    ["newReturnedPoses"] = _poses,
                    ["uncertifiedFits"] = _uncertified,
                    ["circleCalls"] = _circleCalls,
                    ["circleMilliseconds"] = _circleMilliseconds,
                    ["verificationCalls"] = _verificationCalls,
                    ["verificationFailures"] = _verificationFailures,
                    ["verificationMilliseconds"] = _verificationMilliseconds,
                    ["sixFitCountMemoCommitted"] = CountMemoDiagnostic(_committedCountMemo),
                    ["sixFitCountMemoUncommitted"] = CountMemoDiagnostic(_uncommittedCountMemo),
                    ["sixFitCountMemoAllStarted"] = CountMemoDiagnostic(AddCountMemoMetrics(
                        _committedCountMemo, _uncommittedCountMemo)),
                    ["sixFitCountMemoCounterSemantics"] = "logicalRequests counts AddVerified requests; countMatchesCalls counts actual CountMatches invocations, including throwing calls; countMatchesCompleted counts returned calls; factoryInvocations counts executed ConcurrentDictionary factories; factoryCompetitionLosers counts factories whose result lost to another concurrent insertion; stableMemoHits and batchMemoHits are reused Count/Error pairs. These are counts, not timing claims. Committed/uncommitted partition by the ordered six-fit result consumer.",
                    ["fitBatchCount"] = _batchCount,
                    ["fitParallelBatchCount"] = _parallelBatchCount,
                    ["fitSerialBatchCount"] = _serialBatchCount,
                    ["fitSmallBatchSerialCount"] = _smallBatchSerialCount,
                    ["fitExecutionContextSuppressedSerialCount"] = _flowSuppressedSerialCount,
                    ["fitNoUncachedGroupsBatchCount"] = _emptyFitBatchCount,
                    ["fitMaximumWorkers"] = _maximumWorkers,
                    ["bufferedSixGroups"] = _bufferedGroups,
                    ["queuedSixFitGroups"] = _queuedFitGroups,
                    ["computedSixFitGroups"] = _computedFitGroups,
                    ["committedSixFitAttempts"] = _committedFitAttempts,
                    ["failedSixFitGroups"] = _failedFitGroups,
                    ["fitInvocationMillisecondsSum"] = _fitInvocationMillisecondsSum,
                    ["fitInvocationTimingSemantics"] = "Sum of per-group FitSix elapsed measurements; this is not fallback span wall time or process CPU time.",
                    ["batchFitWallMillisecondsSum"] = _batchFitWallMillisecondsSum,
                    ["uncommittedSixGroups"] = _uncommittedGroups,
                    ["uncommittedSixGroupsSemantics"] = "Groups yielded or buffered but not committed in enumeration order; includes cached entries when a later batch item interrupts commit.",
                    ["uncommittedNotStartedSixFitGroups"] = _uncommittedNotStartedFitGroups,
                    ["uncommittedComputedSixFitGroups"] = _uncommittedComputedFitGroups,
                    ["uncommittedCompletedSixFitGroups"] = _uncommittedCompletedFitGroups,
                    ["uncommittedFailedSixFitGroups"] = _uncommittedFailedFitGroups,
                    ["uncommittedFitInvocationMillisecondsSum"] = _uncommittedFitInvocationMillisecondsSum,
                    ["uncommittedCircleCalls"] = _uncommittedCircleCalls,
                    ["uncommittedCircleMilliseconds"] = _uncommittedCircleMilliseconds,
                    ["uncommittedVerificationCalls"] = _uncommittedVerificationCalls,
                    ["uncommittedVerificationMilliseconds"] = _uncommittedVerificationMilliseconds,
                    ["generatingGateEvaluation"] = "Diagnostic-only replay of the exact generating AnchorCenter/AnchorRadius/liveGates predicate over each completed FitSix result list; does not gate, consume, or alter proposals.",
                    ["generatingGateContextAvailable"] = _gateContext is not null,
                    ["generatingGateObservedGroups"] = _observedGateGroups,
                    ["generatingGateUnscoredGroups"] = _unscoredGateGroups,
                    ["generatingGateGroupsAnyPass"] = _groupsAnyGatePass,
                    ["generatingGateGroupsAllFail"] = _groupsAllGateFail,
                    ["generatingGateGroupAllFailSemantics"] = "Counts only completed groups with at least one fit pose and zero passing gate predicates; zero-pose groups are reported separately.",
                    ["generatingGateContextPair"] = _gateContext is null ? null : new[]
                    {
                        new[] { _gateContext.SourceA, _gateContext.QueryA },
                        new[] { _gateContext.SourceB, _gateContext.QueryB }
                    },
                    ["generatingGateGroupsWithoutFitPoses"] = _groupsWithoutFitPoses,
                    ["generatingGateCandidatesPassed"] = _gateCandidatesPassed,
                    ["generatingGateCandidatesFailed"] = _gateCandidatesFailed,
                    ["generatingAnchorCenter"] = _gateContext is null ? null : new
                    {
                        _gateContext.AnchorCenter.X,
                        _gateContext.AnchorCenter.Y
                    },
                    ["generatingAnchorRadius"] = _gateContext?.AnchorRadius,
                    ["generatingLiveGates"] = _gateContext?.LiveGates
                        .Select(gate => new { gate.X, gate.Y }).ToArray(),
                    ["generatingGatePassMaskBit0"] = "FitSix result-list element 0; pass=1, fail=0; zero-fit groups use fitCount=0/mask=0000000000000000.",
                    ["generatingGatePassMaskBitsCovered"] = 64,
                    ["generatingGatePassMaskTruncatedGroups"] = _gateMasksOver64Candidates,
                    ["generatingGatePassMaskHistogram"] = _gateMaskHistogram.Select(item => new
                    {
                        fitCount = item.Key.FitCount,
                        passMask = item.Key.PassMask.ToString("X16"),
                        groups = item.Value
                    }).ToArray(),
                    ["enumerationComplete"] = _enumerationComplete,
                    ["generatingPair"] = new[] { new[] { sourceA, queryA }, new[] { sourceB, queryB } },
                    ["pairScale"] = scale,
                    ["pairTranslation"] = new { translation.X, translation.Y },
                    ["tentativeKey"] = CornerCorrespondenceKey(tentative),
                    ["unfinishedKey"] = _unfinishedKey,
                    ["unfinishedRange"] = _unfinishedRange
                };
                var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                var snapshotReason = !_enumerationComplete ? "interrupted" : _groups >= 500
                    ? "completed-large-fallback" : null;
                if (snapshotReason is not null)
                {
                    details["inputSnapshotReason"] = snapshotReason;
                    details["SourceCorners"] = Snapshot(source);
                    details["QueryCorners"] = Snapshot(query);
                    details["Tentative"] = tentative.Select(m => new { m.Source, m.Query, m.Error }).ToArray();
                }
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Info, "入口六角拟合实际工作量", elapsed, details);
            }
            catch
            {
                // Diagnostic failure must preserve the original fit result or
                // the budget/cancellation exception being propagated.
            }
        }

        private static object[] Snapshot(IReadOnlyList<MapLocalCorner> corners) =>
            corners.Select(c => (object)new
            {
                Point = new { c.Point.X, c.Point.Y },
                RayA = new { c.RayA.X, c.RayA.Y },
                RayB = new { c.RayB.X, c.RayB.Y },
                c.RayALength, c.RayBLength, c.RayAAngleDegrees, c.RayBAngleDegrees,
                c.DirectionDeterminant, c.ContourIndex, c.VertexIndex
            }).ToArray();
    }
}
