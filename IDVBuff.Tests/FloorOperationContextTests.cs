using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using IDVBuff.Pipeline;
using Xunit;

namespace IDVBuff.Tests;

public sealed class FloorOperationContextTests
{
    private static MapMatchSnapshot CreateMatch(
        Guid? matchId = null,
        int version = 1,
        MapMatchState state = MapMatchState.Started)
    {
        return new MapMatchSnapshot(
            state,
            null,
            version,
            "S1",
            matchId ?? Guid.NewGuid());
    }

    private static MapOpenOperationContext CreateContext(
        MapMatchSnapshot match,
        Guid mapId,
        DateTimeOffset updatedAt,
        int toggleVersion = 1,
        long generation = 1,
        string? manualFloorKey = null,
        CancellationToken cancellationToken = default)
    {
        return new MapOpenOperationContext
        {
            OperationMatch = match,
            MapId = mapId,
            MapUpdatedAt = updatedAt,
            MapToggleVersion = toggleVersion,
            OperationGeneration = generation,
            ManualFloorKey = manualFloorKey,
            CancellationToken = cancellationToken
        };
    }

    private static FloorAlignmentAttemptResult CreateAttemptResult(
        string floorKey,
        FloorAlignmentAttemptOutcome outcome,
        MapStructureRejectionReason rejectionReason = MapStructureRejectionReason.None,
        double confidence = 0.85d)
    {
        var diagnostics = new MapScanDiagnostics();
        var map = new MapRecord { Id = Guid.NewGuid(), Title = "TestMap" };
        var attempt = new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureAttempted = true,
            StructureAccepted = outcome == FloorAlignmentAttemptOutcome.Accepted,
            FailureReason = outcome == FloorAlignmentAttemptOutcome.Accepted ? string.Empty : rejectionReason.ToString(),
            StructureResult = new MapStructureRegistrationResult
            {
                Accepted = outcome == FloorAlignmentAttemptOutcome.Accepted,
                RejectionReason = rejectionReason,
                Confidence = confidence,
                Transform = outcome == FloorAlignmentAttemptOutcome.Accepted
                    ? new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
                    : null
            },
            Recognition = outcome == FloorAlignmentAttemptOutcome.Accepted
                ? new RuntimeMapRecognition
                {
                    Map = map,
                    FloorImagePath = "dummy.png",
                    Result = new MapRecognitionResult
                    {
                        MapId = map.Id,
                        Floor = floorKey,
                        Confidence = confidence,
                        OverlayTransform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
                    }
                }
                : null
        };

        return new FloorAlignmentAttemptResult
        {
            FloorKey = floorKey,
            Outcome = outcome,
            Attempt = attempt,
            AlignedRecognition = attempt.Recognition,
            RejectionReason = rejectionReason,
            FailureReason = attempt.FailureReason,
            Diagnostics = diagnostics,
            Confidence = confidence
        };
    }

    [Fact]
    public void MapOpenOperationContext_WhenAllPropertiesMatch_ReturnsTrue()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        Assert.True(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_WhenMatchIdOrVersionMismatch_ReturnsFalse()
    {
        var match = CreateMatch(version: 1);
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        // 新对局（不同的 MatchId）
        var otherMatch = CreateMatch(version: 1);
        Assert.False(context.MatchesCurrentOperation(
            otherMatch,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));

        // 同一对局但版本变更
        var versionChangedMatch = new MapMatchSnapshot(
            match.State,
            match.PlayerSlot,
            match.Version + 1,
            match.MapClass,
            match.MatchId);
        Assert.False(context.MatchesCurrentOperation(
            versionChangedMatch,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));

        // 对局已结束
        var endedMatch = new MapMatchSnapshot(
            MapMatchState.Ended,
            match.PlayerSlot,
            match.Version,
            match.MapClass,
            match.MatchId);
        Assert.False(context.MatchesCurrentOperation(
            endedMatch,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_WhenMapIdMismatch_ReturnsFalse()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var otherMapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: otherMapId,
            currentMapUpdatedAt: updatedAt));

        // 当前未锁定地图（null）
        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: null,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_WhenMapUpdatedAtMismatch_ReturnsFalse()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt.AddSeconds(1)));
    }

    [Fact]
    public void MapOpenOperationContext_WhenToggleClosedOrVersionMismatch_ReturnsFalse()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        // 小地图已关闭
        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: false,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));

        // 小地图版本已改变（例如用户关闭后重新打开）
        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 11,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_WhenGenerationMismatch_ReturnsFalse()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5);

        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 6,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_WhenManualFloorKeyMismatch_ReturnsFalse()
    {
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;

        // 用户显式指定 2f
        var contextManual = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5, manualFloorKey: "2f");
        Assert.True(contextManual.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt,
            currentFloorKey: "2f"));

        // 当前楼层被切换为 1f -> 判定失效
        Assert.False(contextManual.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt,
            currentFloorKey: "1f"));

        // 非手动楼层（自动模式）-> 不受当前楼层变化硬性拒绝
        var contextAuto = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5, manualFloorKey: null);
        Assert.True(contextAuto.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt,
            currentFloorKey: "1f"));
    }

    [Fact]
    public void MapOpenOperationContext_WhenCancelled_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        var match = CreateMatch();
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var context = CreateContext(match, mapId, updatedAt, toggleVersion: 10, generation: 5, cancellationToken: cts.Token);

        Assert.True(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));

        cts.Cancel();

        Assert.False(context.MatchesCurrentOperation(
            match,
            isToggleOpen: true,
            currentToggleVersion: 10,
            currentGeneration: 5,
            currentMapId: mapId,
            currentMapUpdatedAt: updatedAt));
    }

    [Fact]
    public void MapOpenOperationContext_ABAMapSwitchScenario_OldContextIsSuperseded()
    {
        var match = CreateMatch();
        var mapA = Guid.NewGuid();
        var mapB = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        int toggleVersion = 1;

        // 1. 用户最初处于 Map A，生成操作上下文 contextA1
        long generation = 1;
        var contextA1 = CreateContext(match, mapA, updatedAt, toggleVersion, generation);

        // contextA1 当前有效
        Assert.True(contextA1.MatchesCurrentOperation(match, true, toggleVersion, generation, mapA, updatedAt));

        // 2. 用户快速切换到 Map B，递增 generation 并改变 MapId
        generation++; // 2
        var currentMapId = mapB;
        Assert.False(contextA1.MatchesCurrentOperation(match, true, toggleVersion, generation, currentMapId, updatedAt));

        // 3. 用户又快速切回 Map A，再次递增 generation
        generation++; // 3
        currentMapId = mapA;

        // 4. 关键验证：即使当前 MapId 再次是 Map A，contextA1 的 generation (1) 与当前 (3) 不匹配
        // 旧的异步任务返回后，必须判定为 Superseded，绝不允许覆盖切回后的新 Map A 状态
        Assert.False(contextA1.MatchesCurrentOperation(match, true, toggleVersion, generation, currentMapId, updatedAt));

        // 5. 新开图的 contextA2 (generation = 3) 才是合法的
        var contextA2 = CreateContext(match, mapA, updatedAt, toggleVersion, generation);
        Assert.True(contextA2.MatchesCurrentOperation(match, true, toggleVersion, generation, currentMapId, updatedAt));
    }

    [Fact]
    public void FloorAlignmentRecoveryRules_ShortTermPreferenceOrdering_WorksCorrectly()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = "SacredHeart",
            Floors =
            [
                new FloorDefinition { Key = "1f", DisplayName = "1F" },
                new FloorDefinition { Key = "2f", DisplayName = "2F" },
                new FloorDefinition { Key = "3f", DisplayName = "3F" },
                new FloorDefinition { Key = "b1", DisplayName = "B1" }
            ]
        };

        // 初始为 1f，偏好为 3f：剩余备选必须优先包含 3f，其后是 2f, b1
        var candidates = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(map, "1f", recentConfirmedFloorKey: "3f");
        Assert.Equal(["3f", "2f", "b1"], candidates);

        // 初始为 1f，偏好为不在列表中的无效层：按普通降序/升序
        var fallbackCandidates = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(map, "1f", recentConfirmedFloorKey: "invalid");
        Assert.Equal(["2f", "3f", "b1"], fallbackCandidates);
    }

    [Fact]
    public void DecideFromAttempts_AllRejected_WhenBudgetNotExhausted_ReturnsAllRejected()
    {
        var attempt1 = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var attempt2 = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.InconsistentStructure);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt1, attempt2], budgetExhausted: false);
        Assert.Equal(FloorRecoveryResolution.AllRejected, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_BudgetExhausted_WhenNoAccepted_ReturnsInconclusive()
    {
        var attempt1 = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var attempt2 = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.NoCandidate);

        // 超时终止时未能确认全部候选，不能断言 AllRejected，必须返回 Inconclusive
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt1, attempt2], budgetExhausted: true);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_WhenPendingEvidenceExists_ReturnsPendingEvidence()
    {
        var attempt1 = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var attempt2 = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.PendingEvidence);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt1, attempt2], budgetExhausted: false);
        Assert.Equal(FloorRecoveryResolution.PendingEvidence, decision.Resolution);
        Assert.Null(decision.Winner);
    }
}
