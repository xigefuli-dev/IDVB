using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class FloorRecoveryDecisionTests
{
    private static FloorAlignmentAttemptResult Classify(
        FloorAlignmentAttemptResult input,
        MapAlignmentChannel channel = MapAlignmentChannel.Standard,
        bool lowStructureEvidenceAccepted = true,
        bool lowStructurePending = false) =>
        FloorAlignmentRecoveryRules.ClassifyAttemptResult(
            input.Attempt.Recognition?.Map.Id ?? Guid.NewGuid(), input.FloorKey,
            channel, input.Attempt, repairKey: null, minimumStandardConfidence: 0.80d,
            lowStructureEvidenceAccepted, lowStructurePending);

    [Theory]
    [InlineData(MapStructureRejectionReason.InsufficientStructure)]
    [InlineData(MapStructureRejectionReason.QueryLargerThanReference)]
    [InlineData(MapStructureRejectionReason.NoCandidate)]
    [InlineData(MapStructureRejectionReason.WeakAbsoluteScore)]
    [InlineData(MapStructureRejectionReason.AmbiguousCandidates)]
    [InlineData(MapStructureRejectionReason.InconsistentStructure)]
    [InlineData(MapStructureRejectionReason.RefinementFailed)]
    [InlineData(MapStructureRejectionReason.ScaleSearchBoundary)]
    [InlineData(MapStructureRejectionReason.TimeBudgetExceeded)]
    [InlineData(MapStructureRejectionReason.LowGeometricLockConfidence)]
    public void InconclusiveStructureCannotExcludeFloor(MapStructureRejectionReason reason)
    {
        var failure = Classify(CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, reason));
        var accepted = Classify(CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted));

        Assert.Equal(FloorAlignmentAttemptOutcome.Inconclusive, failure.Outcome);
        Assert.Equal(reason, failure.RejectionReason);
        Assert.Null(failure.AlignedRecognition);
        Assert.Null(failure.PendingRepairCacheKey);
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([failure, accepted], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Theory]
    [InlineData(MapStructureRejectionReason.InvalidInput)]
    [InlineData(MapStructureRejectionReason.UnsupportedAlignmentMode)]
    [InlineData(MapStructureRejectionReason.InvalidLockedScale)]
    public void SystemFailureRemainsMissingEvidence(MapStructureRejectionReason reason)
    {
        var failure = Classify(CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, reason));
        Assert.Equal(FloorAlignmentAttemptOutcome.Error, failure.Outcome);
        var accepted = CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted);
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([failure, accepted], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Theory]
    [InlineData("当前选择的地图不存在或未加载。")]
    [InlineData("The selected map does not contain floor '1f'.")]
    [InlineData("The alignment reference for floor '1f' is missing.")]
    [InlineData("The alignment reference for floor '1f' cannot be read.")]
    public void OrdinaryResourceFailureCannotManufactureUniqueWinner(string failureReason)
    {
        var attempt = new MapRecognitionAttempt { FailureReason = failureReason };
        var failure = FloorAlignmentRecoveryRules.ClassifyAttemptResult(Guid.NewGuid(), "1f",
            MapAlignmentChannel.Standard, attempt, null, .80d, true, false);
        Assert.Equal(FloorAlignmentAttemptOutcome.Error, failure.Outcome);
        Assert.Equal(failureReason, failure.FailureReason);
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(false, false, failure));
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            [failure, CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted)], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void TypedTimeoutIsInconclusiveEvenWithoutBudgetText()
    {
        var attempt = new MapRecognitionAttempt
        {
            StructureAttempted = true,
            StructureResult = new MapStructureRegistrationResult
            {
                RejectionReason = MapStructureRejectionReason.TimeBudgetExceeded
            }
        };
        var result = FloorAlignmentRecoveryRules.ClassifyAttemptResult(Guid.NewGuid(), "1f",
            MapAlignmentChannel.Standard, attempt, null, .80d, true, false);
        Assert.Equal(FloorAlignmentAttemptOutcome.Inconclusive, result.Outcome);
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(false, false, result));
    }

    [Fact]
    public void LowStructurePendingCannotExcludeFloorOrPublishTransform()
    {
        var pending = Classify(CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted),
            MapAlignmentChannel.LowStructure, lowStructureEvidenceAccepted: false, lowStructurePending: true);
        Assert.Equal(FloorAlignmentAttemptOutcome.PendingEvidence, pending.Outcome);
        Assert.Null(pending.AlignedRecognition);
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            [pending, CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted)], false);
        Assert.Equal(FloorRecoveryResolution.PendingEvidence, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void Feedback213407_FirstFloorIndicatorCannotBeOverriddenBySecondFloorFit()
    {
        // Supplied output-log-20260928-213121: L1905, L1948, L1975.
        // These are terminal algorithm outputs, not an original-pixel replay.
        var first = Classify(CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.WeakAbsoluteScore));
        var second = Classify(CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted,
            confidence: 0.6590356991972963));
        var basement = Classify(CreateAttemptResult("b1f", FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.NoCandidate, confidence: 0d));

        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(false, false, first,
            detectedFloorKey: "1f"));
        Assert.Equal(FloorAlignmentAttemptOutcome.Accepted, second.Outcome);
        Assert.False(FloorAlignmentRecoveryRules.IsFloorCommitAllowed(second.FloorKey, "1f"));
        foreach (var indicator in new string?[] { "1f", null })
        {
            var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
                [first, second, basement], false, requiredFloorKey: indicator);
            Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
            Assert.Null(decision.Winner);
        }
    }

    [Fact]
    public void Feedback212759_BasementFailureAndAmbiguousSecondFloorCannotCertifyFirstFloor()
    {
        // Supplied output-log-20260928-212214: L2681, L2715, L2733.
        var basement = Classify(CreateAttemptResult("b1f", FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.WeakAbsoluteScore));
        var first = Classify(CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted,
            confidence: 0.6375466853408032));
        var second = Classify(CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.AmbiguousCandidates, confidence: 0.6488702629422877));

        Assert.Equal(FloorAlignmentAttemptOutcome.Inconclusive, second.Outcome);
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(false, false, basement,
            detectedFloorKey: "b1f"));
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([basement, first, second], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Theory]
    [InlineData("1f", "2f")]
    [InlineData("2f", "1f")]
    [InlineData("b1f", "1f")]
    public void HighConfidenceFitCannotOverrideResolvedFloor(string requiredFloor, string otherFloor)
    {
        var rejected = CreateAttemptResult(requiredFloor, FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.OutsideValidBounds);
        var other = CreateAttemptResult(otherFloor, FloorAlignmentAttemptOutcome.Accepted, confidence: .99d);
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([rejected, other], false, requiredFloor);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
        Assert.False(FloorAlignmentRecoveryRules.IsFloorCommitAllowed(otherFloor, requiredFloor));
    }

    [Theory]
    [InlineData("1f", "2f")]
    [InlineData("2f", "1f")]
    [InlineData("1f", "b1f")]
    public void FreshIndicatorAndAcceptedAlignmentStillPermitRealFloorChange(string current, string detected)
    {
        var target = FloorRecognitionRules.ResolveTargetFloor(false, current, detected, "1f");
        var accepted = Classify(CreateAttemptResult(target, FloorAlignmentAttemptOutcome.Accepted));
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([accepted], false, detected);
        Assert.Equal(FloorRecoveryResolution.SingleAccepted, decision.Resolution);
        Assert.Equal(detected, decision.Winner?.FloorKey);
        Assert.True(FloorAlignmentRecoveryRules.IsFloorCommitAllowed(target, detected));
    }

    [Fact]
    public void ManualFloorTakesPrecedenceOverIndicatorAndAlternativeFit()
    {
        var target = FloorRecognitionRules.ResolveTargetFloor(true, "1f", "2f", "1f");
        var accepted = Classify(CreateAttemptResult(target, FloorAlignmentAttemptOutcome.Accepted));
        Assert.False(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(true, false, accepted, "2f"));
        Assert.Equal(FloorRecoveryResolution.SingleAccepted,
            FloorAlignmentRecoveryRules.DecideFromAttempts([accepted], false, requiredFloorKey: target).Resolution);
        Assert.False(FloorAlignmentRecoveryRules.IsFloorCommitAllowed("2f", target));
    }

    [Theory]
    [InlineData(MapStructureRejectionReason.WeakAbsoluteScore)]
    [InlineData(MapStructureRejectionReason.NoCandidate)]
    [InlineData(MapStructureRejectionReason.AmbiguousCandidates)]
    [InlineData(MapStructureRejectionReason.None)]
    public void IncorrectRejectedLabelCannotProveFloorUniqueness(MapStructureRejectionReason reason)
    {
        var misclassified = CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Rejected, reason);
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            [misclassified, CreateAttemptResult("2f", FloorAlignmentAttemptOutcome.Accepted)], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void MissingIndicatorKeepsSameFloorSuccessAndDoesNotExcludeFailedFloor()
    {
        var target = FloorRecognitionRules.ResolveTargetFloor(false, "2f", null, "1f");
        var accepted = Classify(CreateAttemptResult(target, FloorAlignmentAttemptOutcome.Accepted));
        Assert.Equal("2f", FloorAlignmentRecoveryRules.DecideFromAttempts([accepted], false).Winner?.FloorKey);
        var failure = Classify(CreateAttemptResult(target, FloorAlignmentAttemptOutcome.Rejected,
            MapStructureRejectionReason.WeakAbsoluteScore));
        Assert.True(FloorAlignmentRecoveryRules.ShouldAttemptFloorRecovery(false, false, failure));
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts(
            [failure, CreateAttemptResult("1f", FloorAlignmentAttemptOutcome.Accepted)], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }

    [Fact]
    public void EmptyAttemptsAreMissingEvidence()
    {
        var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([], false);
        Assert.Equal(FloorRecoveryResolution.Inconclusive, decision.Resolution);
        Assert.Null(decision.Winner);
    }
}
