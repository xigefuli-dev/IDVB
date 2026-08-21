using IDVBuff.Pipeline;
using Xunit;

namespace IDVBuff.Tests;

public sealed class MapOperationTraceTests
{
    [Fact]
    public void Complete_ClosesTopLevelTimeline_AndMarksSkippedPhases()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.QuickScan,
            ["opening", "compute", "overlay"],
            clock);

        using (trace.StartTopLevel("opening", MapOperationWaitKind.Timer))
        {
            clock.Advance(10);
        }
        using (trace.StartTopLevel("compute", MapOperationWaitKind.Compute))
        {
            clock.Advance(20);
        }
        clock.Advance(1);

        var summary = trace.Complete("success", "completed");

        Assert.Equal(31d, summary.WallClockMs);
        Assert.Equal(30d, summary.CoveredTopLevelMs);
        Assert.Equal(1d, summary.UnaccountedMs);
        Assert.False(summary.ShouldWarnUnaccounted);
        Assert.Contains(
            summary.Spans,
            span => span.Name == "overlay"
                && span.Status == MapOperationSpanStatus.Skipped);
        Assert.DoesNotContain("overlay", summary.ToPhaseTimings().Keys);
        Assert.Equal("skipped", summary.ToPhaseStatuses()["overlay"]);
    }

    [Fact]
    public void Complete_WarnsWhenUnaccountedTimeExceedsThreshold()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.MapOpenAlignment,
            ["phase"],
            clock);

        using (trace.StartTopLevel("phase"))
            clock.Advance(5);
        clock.Advance(4);

        var summary = trace.Complete("success", "completed");

        Assert.Equal(9d, summary.WallClockMs);
        Assert.Equal(4d, summary.UnaccountedMs);
        Assert.True(summary.ShouldWarnUnaccounted);
        Assert.True(summary.UnaccountedRatio > 0.01d);
    }

    [Fact]
    public void MapOpenAlignment_WallClockIncludesResultPublishAndCommitStages()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.MapOpenAlignment,
            [
                "route_prepare",
                "alignment_compute",
                "result_publish",
                "session_commit",
                "persistence",
                "overlay_publish",
                "tracking_start",
                "cleanup"
            ],
            clock);

        using (trace.StartTopLevel("route_prepare"))
            clock.Advance(3);
        using (trace.StartTopLevel("alignment_compute"))
            clock.Advance(40);
        using (trace.StartTopLevel("result_publish"))
            clock.Advance(8);
        using (trace.StartTopLevel("session_commit"))
            clock.Advance(2);
        using (trace.StartTopLevel("persistence"))
            clock.Advance(5);
        using (trace.StartTopLevel("overlay_publish"))
            clock.Advance(4);
        using (trace.StartTopLevel("tracking_start"))
            clock.Advance(1);
        using (trace.StartTopLevel("cleanup"))
            clock.Advance(2);

        var summary = trace.Complete("success", "completed");

        Assert.Equal(65d, summary.WallClockMs);
        Assert.Equal(summary.WallClockMs, summary.CoveredTopLevelMs);
        Assert.Equal(0d, summary.UnaccountedMs);
        Assert.Equal(8d, summary.GetTopLevelDurationMs("result_publish"));
        Assert.Equal("completed", summary.ToPhaseStatuses()["result_publish"]);
    }

    [Fact]
    public void Complete_DetectsTopLevelOverlap()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.QuickScan,
            ["a", "b"],
            clock);

        var first = trace.StartTopLevel("a");
        clock.Advance(2);
        var second = trace.StartTopLevel("b");
        clock.Advance(3);
        first.Complete();
        clock.Advance(1);
        second.Complete();

        var summary = trace.Complete("success", "completed");

        Assert.True(summary.HasTopLevelOverlap);
        Assert.Equal(3d, summary.OverlapMs);
    }

    [Fact]
    public void Complete_ClosesActiveSpanWithTerminalStatus()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.BackgroundScan,
            ["worker"],
            clock);
        _ = trace.StartTopLevel("worker", MapOperationWaitKind.Compute);
        clock.Advance(7);

        var summary = trace.Complete("cancelled", "match-cancellation");
        var span = Assert.Single(summary.Spans, span => span.Name == "worker");

        Assert.Equal(MapOperationSpanStatus.Cancelled, span.Status);
        Assert.Equal(7d, span.DurationMs);
        Assert.Equal("match-cancellation", span.TerminalReason);
    }

    [Fact]
    public void Complete_DistinguishesSkippedFromRealZeroDuration()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.QuickScan,
            ["instant", "never"],
            clock);

        using (trace.StartTopLevel("instant"))
        {
        }

        var summary = trace.Complete("success", "completed");

        var instant = Assert.Single(summary.Spans, span => span.Name == "instant");
        var never = Assert.Single(summary.Spans, span => span.Name == "never");
        Assert.Equal(0d, instant.DurationMs);
        Assert.Equal(MapOperationSpanStatus.Completed, instant.Status);
        Assert.Equal(0d, never.DurationMs);
        Assert.Equal(MapOperationSpanStatus.Skipped, never.Status);
    }

    [Fact]
    public void Complete_KeepsQueueWorkerAndFloorContextIndependent()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.QuickScan,
            ["candidate-a", "candidate-b"],
            clock);

        using (trace.StartTopLevel(
                   "candidate-a",
                   mapId: "map-a",
                   floorKey: "1f",
                   attemptIndex: 0))
        {
            using (trace.StartChild(
                       "dispatch_wait",
                       MapOperationWaitKind.Queue,
                       mapId: "map-a",
                       floorKey: "1f",
                       attemptIndex: 0))
            {
                clock.Advance(7);
            }
            using (trace.StartChild(
                       "worker_execution",
                       MapOperationWaitKind.Compute,
                       mapId: "map-a",
                       floorKey: "1f",
                       attemptIndex: 0))
            {
                clock.Advance(11);
            }
        }

        using (trace.StartTopLevel(
                   "candidate-b",
                   mapId: "map-b",
                   floorKey: "2f",
                   attemptIndex: 1))
        {
            clock.Advance(5);
        }

        var summary = trace.Complete("success", "completed");
        var queue = Assert.Single(summary.Spans, span => span.Name == "dispatch_wait");
        var worker = Assert.Single(summary.Spans, span => span.Name == "worker_execution");
        var candidateA = Assert.Single(summary.Spans, span => span.Name == "candidate-a");
        var candidateB = Assert.Single(summary.Spans, span => span.Name == "candidate-b");

        Assert.Equal(7d, queue.DurationMs);
        Assert.Equal(MapOperationWaitKind.Queue, queue.WaitKind);
        Assert.Equal(11d, worker.DurationMs);
        Assert.Equal(MapOperationWaitKind.Compute, worker.WaitKind);
        Assert.Equal("map-a", queue.MapId);
        Assert.Equal("1f", queue.FloorKey);
        Assert.Equal(0, queue.AttemptIndex);
        Assert.Equal("map-b", candidateB.MapId);
        Assert.Equal("2f", candidateB.FloorKey);
        Assert.Equal(1, candidateB.AttemptIndex);
        Assert.Equal(18d, candidateA.DurationMs);
    }

    [Fact]
    public void ChildSpan_InheritsItsOwnTopLevelFloorContext()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(MapOperationTypes.FloorAlignment, clock: clock);

        using (trace.StartTopLevel(
                   "alignment",
                   mapId: "map-2",
                   floorKey: "2f"))
        using (trace.StartChild("structure_search"))
        {
            clock.Advance(4);
        }

        var summary = trace.Complete("success", "completed");
        var child = Assert.Single(summary.Spans, span => span.Name == "structure_search");

        Assert.Equal("map-2", child.MapId);
        Assert.Equal("2f", child.FloorKey);
    }

    [Fact]
    public void SetContext_DoesNotRetroactivelyReassignEarlierFloorSpans()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(MapOperationTypes.QuickScan, clock: clock);

        using (trace.StartTopLevel("floor-1", floorKey: "1f"))
            clock.Advance(1);
        trace.SetContext(floorKey: "2f");
        using (trace.StartTopLevel("floor-2"))
            clock.Advance(1);

        var summary = trace.Complete("success", "completed");

        Assert.Equal("1f", Assert.Single(summary.Spans, span => span.Name == "floor-1").FloorKey);
        Assert.Equal("2f", Assert.Single(summary.Spans, span => span.Name == "floor-2").FloorKey);
    }

    [Fact]
    public void ToHumanTimeline_IndentsChildSpansAndMarksUserWait()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(
            MapOperationTypes.CandidateConfirmation,
            ["selection"],
            clock);

        using (trace.StartTopLevel("selection", MapOperationWaitKind.User))
        using (trace.StartChild("capture", MapOperationWaitKind.Capture))
        {
            clock.Advance(3);
        }

        var timeline = trace.Complete("success", "completed").ToHumanTimeline();

        Assert.Contains("selection [completed] [user-wait]", timeline);
        Assert.Contains("├─ [+0000.0ms]", timeline);
        Assert.Contains("capture [completed]", timeline);
    }

    [Fact]
    public void Complete_UsesUniqueOperationIdsForTerminalOutcomes()
    {
        var cancelled = new MapOperationTrace(MapOperationTypes.QuickScan);
        var failed = new MapOperationTrace(MapOperationTypes.QuickScan);

        var cancelledSummary = cancelled.Complete("cancelled", "user-cancelled");
        var failedSummary = failed.Complete("failed", "exception:InvalidOperationException");

        Assert.NotEqual(cancelledSummary.OperationId, failedSummary.OperationId);
        Assert.Equal("cancelled", cancelledSummary.Outcome);
        Assert.Equal(MapOperationSpanStatus.Failed, MapOperationTraceStatus(failedSummary));
        Assert.Equal("exception:InvalidOperationException", failedSummary.TerminalReason);
    }

    [Fact]
    public async Task QueueSpan_CompletedOnWorkerThenCaller_RestoresCallerParent()
    {
        var clock = new FakeClock();
        var trace = new MapOperationTrace(MapOperationTypes.QuickScan, clock: clock);
        var queue = trace.StartChild("dispatch_wait", MapOperationWaitKind.Queue);

        await Task.Run(() => queue.Complete());
        queue.Complete();
        using (trace.StartChild("after_dispatch"))
            clock.Advance(1);

        var summary = trace.Complete("success", "completed");
        var child = Assert.Single(summary.Spans, span => span.Name == "after_dispatch");

        Assert.Null(child.ParentSpanId);
    }

    private static MapOperationSpanStatus MapOperationTraceStatus(
        MapOperationTraceSummary summary)
    {
        var status = summary.Outcome switch
        {
            "cancelled" => MapOperationSpanStatus.Cancelled,
            "superseded" => MapOperationSpanStatus.Superseded,
            "failed" => MapOperationSpanStatus.Failed,
            _ => MapOperationSpanStatus.Completed
        };
        return status;
    }

    private sealed class FakeClock : IMapOperationClock
    {
        public long Timestamp { get; private set; }
        public long Frequency => 1000;

        public void Advance(double milliseconds) =>
            Timestamp += checked((long)Math.Round(milliseconds));
    }
}
