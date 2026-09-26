using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using IDVBuff.Pipeline;
using Xunit;

namespace IDVBuff.Tests;

public sealed class FloorRecoveryDecisionTests
{
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
    public void ShouldAttemptFloorRecovery_RequiresMultiFloor_NonManual_AndEligibleRejection()
    {
        var eligibleAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var acceptedAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted);
        var timeoutAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Inconclusive, MapStructureRejectionReason.None);
        var scaleLargeAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.ScaleChangeTooLarge);

        // 手动楼层绝不触发
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: true, isSingleFloorMap: false, eligibleAttempt));
        // 单楼层地图绝不触发
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: true, eligibleAttempt));
        // 已接受成功绝不触发
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, acceptedAttempt));
        // 非 Rejected（如超时）绝不触发
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, timeoutAttempt));
        // 非白名单拒绝理由绝不触发
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, scaleLargeAttempt));

        // 仅在白名单结构拒绝下触发
        Assert.True(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, eligibleAttempt));

        var noCandAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.NoCandidate);
        Assert.True(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, noCandAttempt));

        var inconsistentAttempt = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.InconsistentStructure);
        Assert.True(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(isManualFloor: false, isSingleFloorMap: false, inconsistentAttempt));
    }

    [Fact]
    public void ResolveAlternativeFloors_PrioritizesRecentConfirmedPreference()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = "Hospital",
            Floors =
            [
                new FloorDefinition { Key = "1f", DisplayName = "1F" },
                new FloorDefinition { Key = "2f", DisplayName = "2F" },
                new FloorDefinition { Key = "b1", DisplayName = "B1" }
            ]
        };

        // 初始为 2f，无偏好：剩下 1f, b1
        var candidates1 = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(map, "2f", recentConfirmedFloorKey: null);
        Assert.Equal(["1f", "b1"], candidates1);

        // 初始为 2f，有偏好 b1：b1 排第一，其次 1f
        var candidates2 = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(map, "2f", recentConfirmedFloorKey: "b1");
        Assert.Equal(["b1", "1f"], candidates2);

        // 偏好如果是当前 initial floor（2f），不应重复包含在备选中
        var candidates3 = FloorAlignmentRecoveryRules.ResolveAlternativeFloors(map, "2f", recentConfirmedFloorKey: "2f");
        Assert.Equal(["1f", "b1"], candidates3);
    }

    [Fact]
    public void DecideFromAttempts_SingleAccepted_ReturnsWinner()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted, confidence: 0.88d);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.SingleAccepted, decision.Resolution);
        Assert.NotNull(decision.Winner);
        Assert.Equal("1f", decision.Winner.FloorKey);
    }

    [Fact]
    public void DecideFromAttempts_MultipleAccepted_ReturnsAmbiguous()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted, confidence: 0.82d);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted, confidence: 0.88d);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.Ambiguous, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_OneAccepted_OnePending_DoesNotDeclareWinner()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.PendingEvidence);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        // 另一层还在等证据，不能宣布 1f 唯一！
        Assert.Equal(FloorRecoveryResolution.PendingEvidence, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_OneAccepted_OneInconclusive_DoesNotDeclareWinner()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Inconclusive);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_AllRejected_ReturnsAllRejected()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.WeakAbsoluteScore);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, MapStructureRejectionReason.InconsistentStructure);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.AllRejected, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Theory]
    [InlineData(FloorAlignmentAttemptOutcome.Accepted)]
    [InlineData(FloorAlignmentAttemptOutcome.Rejected)]
    public void DecideFromAttempts_FailedCandidateCannotProveFloorUniqueness(
        FloorAlignmentAttemptOutcome otherOutcome)
    {
        var unavailable = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Error);
        var other = CreateAttemptResult("1f", otherOutcome);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            [unavailable, other], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void DecideFromAttempts_AnySuperseded_ReturnsSuperseded()
    {
        var attempt2f = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Superseded);
        var attempt1f = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted);

        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([attempt2f, attempt1f], budgetExhausted: false);

        Assert.Equal(FloorRecoveryResolution.Superseded, decision.Resolution);
    }

    [Fact]
    public void IsAttemptFullyAccepted_RejectsReusedTransformAndForcedBest()
    {
        var mapId = Guid.NewGuid();
        var normalAttempt = new MapRecognitionAttempt
        {
            StructureAccepted = true,
            StructureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                Confidence = 0.85d,
                Transform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
            },
            Recognition = new RuntimeMapRecognition
            {
                Map = new MapRecord { Id = mapId },
                FloorImagePath = "dummy.png",
                Result = new MapRecognitionResult
                {
                    MapId = mapId,
                    Floor = "1f",
                    Confidence = 0.85d,
                    OverlayTransform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
                }
            }
        };

        // 正常合格
        Assert.True(FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            normalAttempt,
            MapAlignmentChannel.Standard,
            mapId,
            "1f",
            minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted: true));

        // ReusedLastTransform -> 必须拒绝
        var reusedAttempt = new MapRecognitionAttempt
        {
            StructureAccepted = true,
            StructureResult = normalAttempt.StructureResult,
            Recognition = new RuntimeMapRecognition
            {
                Map = new MapRecord { Id = mapId },
                FloorImagePath = "dummy.png",
                Result = new MapRecognitionResult
                {
                    MapId = mapId,
                    Floor = "1f",
                    Confidence = 0.85d,
                    ReusedLastTransform = true,
                    OverlayTransform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
                }
            }
        };
        Assert.False(FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            reusedAttempt,
            MapAlignmentChannel.Standard,
            mapId,
            "1f",
            minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted: true));

        // WasForcedBestCandidate -> 必须拒绝
        var forcedAttempt = new MapRecognitionAttempt
        {
            StructureAccepted = true,
            StructureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                WasForcedBestCandidate = true,
                Confidence = 0.85d,
                Transform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
            },
            Recognition = normalAttempt.Recognition
        };
        Assert.False(FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            forcedAttempt,
            MapAlignmentChannel.Standard,
            mapId,
            "1f",
            minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted: true));
    }

    [Fact]
    public void IsAttemptFullyAccepted_LowStructureSeparation()
    {
        var mapId = Guid.NewGuid();
        var lowStructAttempt = new MapRecognitionAttempt
        {
            StructureAccepted = true,
            StructureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                Confidence = 0.65d, // 低结构通道可能置信度在 0.65，标准门槛可能是 0.80
                Transform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
            },
            Recognition = new RuntimeMapRecognition
            {
                Map = new MapRecord { Id = mapId },
                FloorImagePath = "dummy.png",
                Result = new MapRecognitionResult
                {
                    MapId = mapId,
                    Floor = "2f",
                    Confidence = 0.65d,
                    OverlayTransform = new MapOverlayTransform { ScaleX = 1.0, ScaleY = 1.0, OffsetX = 10, OffsetY = 20 }
                }
            }
        };

        // 如果跨帧证据未通过（lowStructureEvidenceAccepted = false）-> 拒绝
        Assert.False(FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            lowStructAttempt,
            MapAlignmentChannel.LowStructure,
            mapId,
            "2f",
            minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted: false));

        // 如果跨帧证据通过 -> 接受，不受 minimumStandardConfidence (0.80) 限制
        Assert.True(FloorAlignmentRecoveryRules.IsAttemptFullyAccepted(
            lowStructAttempt,
            MapAlignmentChannel.LowStructure,
            mapId,
            "2f",
            minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted: true));
    }
}
