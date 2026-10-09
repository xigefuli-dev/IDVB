using System.Diagnostics;
using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    internal sealed class GeneratingGateDiagnosticContext
    {
        private readonly IReadOnlyList<Point2d> _liveGates;

        internal int SourceA { get; }
        internal int QueryA { get; }
        internal int SourceB { get; }
        internal int QueryB { get; }
        internal Point2d AnchorCenter { get; }
        internal double AnchorRadius { get; }
        internal IReadOnlyList<Point2d> LiveGates => _liveGates;

        internal GeneratingGateDiagnosticContext(int sourceA, int queryA, int sourceB, int queryB,
            Point2d anchorCenter, double anchorRadius, IReadOnlyList<Point2d> liveGates)
        {
            SourceA = sourceA;
            QueryA = queryA;
            SourceB = sourceB;
            QueryB = queryB;
            AnchorCenter = anchorCenter;
            AnchorRadius = anchorRadius;
            _liveGates = Array.AsReadOnly(liveGates.ToArray());
        }
    }

    private sealed partial class CornerFitMemo
    {
        internal CornerSearchDiagnostics? SearchDiagnostics { get; init; }
        internal GeneratingGateDiagnosticContext? CurrentGeneratingGateContext { get; private set; }

        internal void SetGeneratingGateDiagnosticContext(int sourceA, int queryA, int sourceB,
            int queryB, Point2d anchorCenter, double anchorRadius,
            IReadOnlyList<Point2d> liveGates)
        {
            try
            {
                CurrentGeneratingGateContext = SearchDiagnostics is null
                    ? null : new GeneratingGateDiagnosticContext(sourceA, queryA, sourceB,
                        queryB, anchorCenter, anchorRadius, liveGates);
            }
            catch { CurrentGeneratingGateContext = null; }
        }

        internal void ClearGeneratingGateDiagnosticContext()
        {
            try { CurrentGeneratingGateContext = null; }
            catch { }
        }
    }

    internal sealed class CornerSearchDiagnostics
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly int _queryCorners;
        private int _referenceCorners;
        private long _pairSeeds, _fitMeasuredYielded, _generatingGateKept, _generatingGateRejected;
        private long _seedsAdded, _seedsReplaced, _seedsMergedUnchanged;
        private long _enteredFinalSeeds, _recoveryRefineCalls, _recoveryRefineCacheHits;
        private long _recoveryRefineComputeStarts, _finalRefineCalls, _finalRefineCacheHits;
        private long _finalRefineComputeStarts, _posePoolCandidates, _posesAdded, _posesReplaced;
        private long _posesMergedUnchanged;
        private int _posePoolCount;
        private bool _recoveryComplete, _finalIterationComplete;
        private bool? _returnedResultComplete;
        private int? _returnedPoseCount;
        private string? _returnedFailureReason;
        private string _terminalState = "running";
        private string? _terminalExceptionType;

        private CornerSearchDiagnostics(int queryCorners) => _queryCorners = queryCorners;

        internal static CornerSearchDiagnostics? Start(int queryCorners)
        {
            try { return MapLogCollector.Instance.IsEnabled ? new(queryCorners) : null; }
            catch { return null; }
        }

        internal void SetReferenceCornerCount(int count)
        {
            try { _referenceCorners = count; }
            catch { }
        }

        internal void RecordPairSeed() { try { _pairSeeds++; } catch { } }
        internal void RecordFitMeasuredYield() { try { _fitMeasuredYielded++; } catch { } }

        internal void RecordGeneratingGateResult(bool passed)
        {
            try
            {
                if (passed) _generatingGateKept++;
                else _generatingGateRejected++;
            }
            catch { }
        }

        internal void RecordSeedAdded() { try { _seedsAdded++; } catch { } }
        internal void RecordSeedReplaced() { try { _seedsReplaced++; } catch { } }
        internal void RecordSeedMergedUnchanged() { try { _seedsMergedUnchanged++; } catch { } }
        internal void RecordRecoveryComplete() { try { _recoveryComplete = true; } catch { } }
        internal void RecordEnteredFinalSeed() { try { _enteredFinalSeeds++; } catch { } }
        internal void RecordRecoveryRefineCall() { try { _recoveryRefineCalls++; } catch { } }
        internal void RecordRecoveryRefineCacheHit() { try { _recoveryRefineCacheHits++; } catch { } }
        internal void RecordRecoveryRefineComputeStart() { try { _recoveryRefineComputeStarts++; } catch { } }
        internal void RecordFinalRefineCall() { try { _finalRefineCalls++; } catch { } }
        internal void RecordFinalRefineCacheHit() { try { _finalRefineCacheHits++; } catch { } }
        internal void RecordFinalRefineComputeStart() { try { _finalRefineComputeStarts++; } catch { } }
        internal void RecordFinalIterationComplete() { try { _finalIterationComplete = true; } catch { } }

        internal void RecordPoseAdded(int poolCount)
        {
            try { _posePoolCandidates++; _posesAdded++; _posePoolCount = poolCount; }
            catch { }
        }

        internal void RecordPoseReplaced(int poolCount)
        {
            try { _posePoolCandidates++; _posesReplaced++; _posePoolCount = poolCount; }
            catch { }
        }

        internal void RecordPoseMergedUnchanged(int poolCount)
        {
            try { _posePoolCandidates++; _posesMergedUnchanged++; _posePoolCount = poolCount; }
            catch { }
        }

        internal void RecordReturnedResult(MapLocalGeometrySearch result)
        {
            try
            {
                _terminalState = "returned";
                _returnedResultComplete = result.Complete;
                _returnedPoseCount = result.Poses.Count;
                _returnedFailureReason = result.FailureReason;
            }
            catch { }
        }

        internal void RecordThrown(string state, string? exceptionType)
        {
            try { _terminalState = state; _terminalExceptionType = exceptionType; }
            catch { }
        }

        internal void Write()
        {
            try
            {
                var details = new Dictionary<string, object?>
                {
                    ["diagnosticBoundary"] = "SolveCore output; the public Solve wrapper may map a budget exception to its own incomplete result.",
                    ["queryCornerCount"] = _queryCorners,
                    ["referenceCornerCount"] = _referenceCorners,
                    ["pairSeedCount"] = _pairSeeds,
                    ["fitMeasuredYieldedCount"] = _fitMeasuredYielded,
                    ["generatingGateYieldCounterSemantics"] = "Actual yielded values consumed by the existing FitMeasured caller; kept/rejected uses the shared exact existing predicate.",
                    ["generatingGateKeptYieldCount"] = _generatingGateKept,
                    ["generatingGateRejectedYieldCount"] = _generatingGateRejected,
                    ["seedAddCalls"] = _seedsAdded + _seedsReplaced + _seedsMergedUnchanged,
                    ["seedAddedCount"] = _seedsAdded,
                    ["seedReplacedCount"] = _seedsReplaced,
                    ["seedMergedUnchangedCount"] = _seedsMergedUnchanged,
                    ["recoveryComplete"] = _recoveryComplete,
                    ["finalIterationComplete"] = _finalIterationComplete,
                    ["enteredFinalSeedCount"] = _enteredFinalSeeds,
                    ["pairRecoveryRefineCallCount"] = _recoveryRefineCalls,
                    ["pairRecoveryRefineCacheHitCount"] = _recoveryRefineCacheHits,
                    ["pairRecoveryRefineComputeStartCount"] = _recoveryRefineComputeStarts,
                    ["finalSeedRefineCallCount"] = _finalRefineCalls,
                    ["finalSeedRefineCacheHitCount"] = _finalRefineCacheHits,
                    ["finalSeedRefineComputeStartCount"] = _finalRefineComputeStarts,
                    ["finalRefineCounterSemantics"] = "enteredFinalSeedCount precedes cancellation/budget checks; callCount is RefineSeed entry; cacheHitCount counts successful dictionary lookup; computeStartCount is immediately before LocalRefiner.Refine.",
                    ["refinementPosePoolCandidateCount"] = _posePoolCandidates,
                    ["refinementPoseAddedCount"] = _posesAdded,
                    ["refinementPoseReplacedCount"] = _posesReplaced,
                    ["refinementPoseMergedUnchangedCount"] = _posesMergedUnchanged,
                    ["partialPosePoolCount"] = _posePoolCount,
                    ["posePoolCounterSemantics"] = "Counts actual AddPose calls and outcomes; partialPosePoolCount is the local pool size, while returnedPoseCount is the actual SolveCore result list count.",
                    ["returnedResultComplete"] = _returnedResultComplete,
                    ["returnedPoseCount"] = _returnedPoseCount,
                    ["returnedFailureReason"] = _returnedFailureReason,
                    ["terminalState"] = _terminalState,
                    ["terminalExceptionType"] = _terminalExceptionType
                };
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Info, "入口角点求解诊断", Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    details);
            }
            catch { }
        }
    }
}
