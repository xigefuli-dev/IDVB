using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Tests;

public sealed partial class AdaptiveScaleInitialStreakTests
{
    [Theory]
    [Trait("Category", "IssueRegression")]
    [Trait("Issue", "13")]
    [InlineData("cancelled")]
    [InlineData("closed")]
    [InlineData("reopened")]
    [InlineData("map")]
    [InlineData("revision")]
    [InlineData("floor")]
    [InlineData("generation")]
    [InlineData("match")]
    public async Task LateScaleResetCannotRestoreSupersededAdaptiveState(string supersededBy)
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-continuation-");
        var coordinator = Coordinator(Store(directory));
        using var frame = Frame();
        using var cancellation = new CancellationTokenSource();
        var map = Map();
        var recognition = Recognition(map, "1f", 1);
        var match = new MapMatchSnapshot(MapMatchState.Started, null, 1, map.Class, Guid.NewGuid());
        var currentMatch = match;
        var currentMapId = map.Id;
        var currentRevision = map.UpdatedAt;
        var currentFloor = "1f";
        var currentGeneration = 1L;
        var currentToggle = 1;
        var isOpen = true;
        var context = new MapOpenOperationContext
        {
            OperationMatch = match, MapId = map.Id, MapUpdatedAt = map.UpdatedAt,
            MapToggleVersion = 1, OperationGeneration = 1, ManualFloorKey = "1f",
            CancellationToken = cancellation.Token
        };
        bool IsCurrent() => context.MatchesCurrentOperation(currentMatch, isOpen, currentToggle,
            currentGeneration, currentMapId, currentRevision, currentFloor);
        var resetFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coverageWrites = 0;
        Task<AdaptiveAlignmentDecision>? pending = null;
        try
        {
            coordinator.EvaluateInitial(recognition, frame, null, Evidence(1), 1);
            Assert.True(coordinator.TryGetActiveKey(out var oldKey));
            pending = MapOperationContinuation.CommitAfterAsync(async () =>
            {
                await coordinator.ResetForScaleRecoveryAsync(oldKey);
                resetFinished.TrySetResult();
                await resume.Task;
            }, () =>
            {
                coverageWrites++;
                return coordinator.EvaluateInitial(recognition, frame, null, Evidence(2), currentToggle);
            }, cancellation.Token, IsCurrent);
            await resetFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (supersededBy)
            {
                case "cancelled": cancellation.Cancel(); break;
                case "closed": isOpen = false; break;
                case "reopened": currentToggle++; break;
                case "map": currentMapId = Guid.NewGuid(); break;
                case "revision": currentRevision = currentRevision.AddSeconds(1); break;
                case "floor": currentFloor = "2f"; break;
                case "generation": currentGeneration++; break;
                case "match": currentMatch = match with { Version = 2 }; break;
            }
            coordinator.EndOpen(1, supersededBy);
            resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, coverageWrites);
            Assert.False(coordinator.TryGetActiveKey(out _));
        }
        finally
        {
            cancellation.Cancel();
            resume.TrySetResult();
            if (pending is not null)
                try { await pending; } catch (OperationCanceledException) { }
            await coordinator.DrainAsync();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "IssueRegression")]
    [Trait("Issue", "13")]
    public async Task CurrentScaleResetContinuationStillCommitsAdaptiveEvidence()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-current-");
        var coordinator = Coordinator(Store(directory));
        using var frame = Frame();
        var map = Map();
        var recognition = Recognition(map, "1f", 1);
        var key = AdaptiveScaleKey.Create(map, "1f", frame.ClientBounds, frame.ViewportBounds);
        try
        {
            var decision = await MapOperationContinuation.CommitAfterAsync(
                () => coordinator.ResetForScaleRecoveryAsync(key),
                () => coordinator.EvaluateInitial(recognition, frame, null, Evidence(1), 1),
                default, () => true);
            Assert.Equal(1, decision.ConsecutiveHighQualityCount);
            Assert.True(coordinator.TryGetActiveKey(out var active));
            Assert.Equal(key, active);
        }
        finally
        {
            await coordinator.DrainAsync();
            directory.Delete(recursive: true);
        }
    }
}
