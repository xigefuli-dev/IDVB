using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class LowStructureAlignmentEvidenceTests
{
    [Theory]
    [InlineData(MapAlignmentChannel.Standard, null, false, true)]
    [InlineData(MapAlignmentChannel.LowStructure, null, true, false)]
    [InlineData(MapAlignmentChannel.LowStructure, 1.57476884614911, false, false)]
    [InlineData(MapAlignmentChannel.LowStructure, 1.57476884614911, true, true)]
    [InlineData(MapAlignmentChannel.LowStructure, double.NaN, true, false)]
    [InlineData(MapAlignmentChannel.LowStructure, 0d, true, false)]
    [InlineData(MapAlignmentChannel.LowStructure, 9d, true, false)]
    public void BasementFastSolverRequiresValidatedSupportedFloorScale(
        MapAlignmentChannel channel, double? scale, bool validated, bool expected) =>
        Assert.Equal(expected, MapCvRecognitionService.CanUseVpsg3(channel, scale, validated));

    [Theory]
    [InlineData("", true, false)]
    [InlineData("CachedFixed", true, false)]
    [InlineData("CachedFixed", false, true)]
    public void ValidatedFixedScaleGeometryReachesFloorCommitWithoutNewScaleVotes(
        string route, bool warmStateHit, bool validatedScaleSeed)
    {
        var attempt = Attempt(route, warmStateHit, validatedScaleSeed);
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
        Assert.True(evidence.Accepted);
        Assert.False(evidence.Pending);
        Assert.Equal(0, evidence.Count);
        var classified = Classify(attempt, evidence);
        Assert.Equal(FloorAlignmentAttemptOutcome.Accepted, classified.Outcome);
        Assert.Same(attempt.Recognition, classified.AlignedRecognition);
        Assert.Equal("b1f", FloorAlignmentRecoveryRules.DecideFromAttempts(
            [classified], false, requiredFloorKey: "b1f").Winner?.FloorKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CachedFixed")]
    public void UnconfirmedFixedScaleStillWaitsAndNeverVotes(string route)
    {
        var attempt = Attempt(route);
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
        Assert.True(evidence.Pending);
        Assert.Equal(0, evidence.Count);
        var classified = Classify(attempt, evidence);
        Assert.Equal(FloorAlignmentAttemptOutcome.PendingEvidence, classified.Outcome);
        Assert.Null(classified.AlignedRecognition);
    }

    [Theory]
    [InlineData("SparseCoarseSeed")]
    [InlineData("IncrementalRecovery")]
    [InlineData("ShapeSeed")]
    public void IndependentSearchRetainsItsScaleVote(string route)
    {
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(Attempt(route));
        Assert.True(evidence.Accepted);
        Assert.False(evidence.Pending);
        Assert.Equal(1, evidence.Count);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void TrustedScaleCannotAuthorizeFailedForcedOrReusedGeometry(
        bool accepted, bool forced, bool reused)
    {
        var attempt = Attempt("CachedFixed", warmStateHit: true,
            accepted: accepted, forced: forced, reused: reused);
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
        Assert.False(evidence.Accepted);
        Assert.Equal(0, evidence.Count);
        Assert.NotEqual(FloorAlignmentAttemptOutcome.Accepted, Classify(attempt, evidence).Outcome);
    }

    [Theory]
    [InlineData("1f", "b1f")]
    [InlineData("b1f", "1f")]
    public void ReliableFixedScaleDoesNotOverrideFloorIndicator(string candidate, string required)
    {
        var attempt = Attempt("CachedFixed", warmStateHit: true, floor: candidate);
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
        var classified = Classify(attempt, evidence);
        Assert.Null(FloorAlignmentRecoveryRules.DecideFromAttempts(
            [classified], false, required).Winner);
    }

    [Fact]
    public void ReliableFixedScaleDoesNotOverrideMapIdentity()
    {
        var attempt = Attempt("CachedFixed", warmStateHit: true);
        var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
        var classified = FloorAlignmentRecoveryRules.ClassifyAttemptResult(
            Guid.NewGuid(), "b1f", MapAlignmentChannel.LowStructure, attempt,
            null, .80d, evidence.Accepted, evidence.Pending);
        Assert.NotEqual(FloorAlignmentAttemptOutcome.Accepted, classified.Outcome);
    }

    [Theory]
    [InlineData(MapAlignmentChannel.LowStructure, null, false)]
    [InlineData(MapAlignmentChannel.LowStructure, "", false)]
    [InlineData(MapAlignmentChannel.LowStructure, "CachedFixed", false)]
    [InlineData(MapAlignmentChannel.LowStructure, "SparseCoarseSeed", true)]
    [InlineData(MapAlignmentChannel.Standard, "", true)]
    public void MissingLowStructureRouteCannotCountAsIndependentScaleEvidence(
        MapAlignmentChannel channel, string? route, bool expected) =>
        Assert.Equal(expected, LowStructureScaleEvidenceRules.IsIndependentScaleEvidence(channel, route));

    private static FloorAlignmentAttemptResult Classify(
        MapRecognitionAttempt attempt, LowStructureScaleEvidenceRules.AlignmentEvidence evidence) =>
        FloorAlignmentRecoveryRules.ClassifyAttemptResult(
            attempt.Recognition!.Map.Id, attempt.Recognition.Result.Floor,
            MapAlignmentChannel.LowStructure, attempt, null, .80d,
            evidence.Accepted, evidence.Pending);

    private static MapRecognitionAttempt Attempt(
        string route, bool warmStateHit = false, bool validatedScaleSeed = false,
        bool accepted = true, bool forced = false, bool reused = false, string floor = "b1f")
    {
        var map = new MapRecord { Id = Guid.NewGuid() };
        // First rejected warm B1F operation from build 6036, 18:42:44.
        var transform = new MapOverlayTransform
        {
            ScaleX = 1.57476884614911, ScaleY = 1.57476884614911,
            OffsetX = 913.8879799997537, OffsetY = -4.0097792299451385,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        return new MapRecognitionAttempt
        {
            StructureAccepted = accepted,
            StructureResult = new MapStructureRegistrationResult
            {
                Accepted = accepted, WasForcedBestCandidate = forced, Transform = transform,
                Confidence = .8234230366910095,
                RejectionReason = accepted ? MapStructureRejectionReason.None
                    : MapStructureRejectionReason.WeakAbsoluteScore
            },
            Diagnostics = new MapScanDiagnostics
            {
                LowStructureRoute = route, WarmStateHit = warmStateHit,
                LowStructureValidatedScaleSeed = validatedScaleSeed
            },
            Recognition = new RuntimeMapRecognition
            {
                Map = map,
                Result = new MapRecognitionResult
                {
                    MapId = map.Id, Floor = floor, OverlayTransform = transform,
                    ReusedLastTransform = reused, Confidence = .8234230366910095,
                    LocalizationConfidence = .8234230366910095
                }
            }
        };
    }
}
