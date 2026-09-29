using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanIdentitySafetyTests
{
    [Fact]
    public void DecisionSeparatesExcludedUnverifiedCompetitionAndMissingAlignment()
    {
        var excluded = Candidate(ScanIdentityState.Excluded, 1);
        Assert.Equal("all-identities-excluded", ScanIdentityVerifier.EvaluateSelection([excluded], true, true).Reason);
        var pending = Candidate(ScanIdentityState.Unverified, 1);
        Assert.Equal("unverified-identities", ScanIdentityVerifier.EvaluateSelection([pending], true, true).Reason);
        var first = Candidate(ScanIdentityState.Supported, 1);
        var second = Candidate(ScanIdentityState.Supported, 1);
        Assert.Equal("competing-supported-identities", ScanIdentityVerifier.EvaluateSelection([first, second], true, true).Reason);
        first.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Assert.Equal("supported-without-confirmed-alignment", ScanIdentityVerifier.EvaluateSelection([first], true, true).Reason);
        Assert.Equal("retrieval-incomplete", ScanIdentityVerifier.EvaluateSelection([first], false, true).Reason);
        Assert.Equal("execution-unavailable", ScanIdentityVerifier.EvaluateSelection([first], true, false).Reason);
    }

    [Fact]
    public async Task TrackingTaskDoesNotInheritScanDeadlineOrFrame()
    {
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Fast);
        Task<ScanExecutionContext?> child;
        using (ScanExecutionContext.Suppress())
            child = Task.Run(() => ScanExecutionContext.Current);
        Assert.Same(scan, ScanExecutionContext.Current);
        Assert.Null(await child);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void OneWrongRoomCannotHideBehindGlobalSupport(ScanPerformanceMode mode)
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, new Scalar(30, 25, 22));
        foreach (var room in new[] { new Rect(80, 130, 200, 140), new Rect(390, 120, 250, 200),
            new Rect(120, 400, 250, 120), new Rect(680, 380, 40, 40) })
            Cv2.Rectangle(image, room, new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var reference = frame.Observation.ObservedEdges.Clone();
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        Assert.Equal(ScanIdentityState.Supported,
            ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), transform, viewport, null).State);
        using var wrong = reference.Clone();
        Cv2.Rectangle(wrong, new Rect(665, 365, 75, 75), Scalar.Black, -1);
        using var context = ScanExecutionContext.Enter(mode);
        var conflict = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(wrong), transform, viewport, context);
        Assert.Equal(ScanIdentityState.Excluded, conflict.State);
        Assert.True(conflict.SupportedFraction > ScanIdentityVerifier.MinimumSupport);
        Assert.True(conflict.LongestConflictPixels >= ScanIdentityVerifier.MaximumContinuousConflictPixels);
        Assert.NotNull(conflict.ConflictStart);
        Assert.NotNull(conflict.ConflictEnd);
        Assert.Equal(1, conflict.EvaluatedScale);
        Assert.InRange(conflict.ConflictStart!.Value.X, 665, 740);
        Assert.InRange(conflict.ConflictStart.Value.Y, 365, 440);
        using var serialized = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(conflict));
        Assert.Equal(conflict.ConflictStart.Value.X,
            serialized.RootElement.GetProperty("ConflictStart").GetProperty("X").GetInt32());
        var moved = new MapOverlayTransform { ScaleX = 1, ScaleY = 1, OffsetX = 50 };
        Assert.NotEqual(ScanIdentityState.Supported,
            ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), moved, viewport, context).State);
    }

    [Fact]
    public void SupersededOrCancelledScanCannotContinueValidation()
    {
        var current = true;
        using var cancellation = new CancellationTokenSource();
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Quality, cancellation.Token, () => current);
        Assert.True(scan.CanCompute);
        current = false;
        Assert.True(scan.Expired);
        Assert.False(scan.CanCompute);
        current = true;
        cancellation.Cancel();
        Assert.False(scan.CanCompute);
    }

    [Fact]
    public void WeakSharpSemanticWallSurvivesWhileSmoothFogRemainsNeutral()
    {
        using var sharp = new Mat(240, 320, MatType.CV_8UC3, new Scalar(78, 70, 63));
        Cv2.Rectangle(sharp, new Rect(70, 70, 160, 100), new Scalar(106, 94, 84), -1);
        using var soft = new Mat();
        Cv2.GaussianBlur(sharp, soft, new Size(61, 61), 12);
        using var wall = Vpsg3FastLiveExtractor.Extract(sharp);
        using var fog = Vpsg3FastLiveExtractor.Extract(soft);
        Assert.True(wall.EdgePixelCount > 250, $"Weak wall had only {wall.EdgePixelCount} points");
        Assert.True(fog.EdgePixelCount < wall.EdgePixelCount / 5, $"Fog produced {fog.EdgePixelCount} points");
    }
    [Fact]
    public void UnfinishedCompetitorCannotBeHiddenByFirstSupportedResult()
    {
        var winner = Candidate(ScanIdentityState.Supported, 1);
        var pending = Candidate(ScanIdentityState.Unverified, 0);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, pending], true, true));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner], false, true));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner], true, false));
        pending.IdentityEvidence = pending.IdentityEvidence with { State = ScanIdentityState.Excluded };
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, pending], true, true));
    }

    [Fact]
    public void BetterFitDoesNotDisproveAnotherSupportedIdentity()
    {
        var first = Candidate(ScanIdentityState.Supported, 2);
        var second = Candidate(ScanIdentityState.Supported, .4);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([first, second], true, true));
    }

    [Fact]
    public void ManualClosedSetScanCanSelectClearlyDominantSupportedIdentity()
    {
        var weaker = Candidate(ScanIdentityState.Supported, 1.2);
        var winner = Candidate(ScanIdentityState.Supported, .4);

        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity(
            [weaker, winner], true, true, selectionPolicy: ScanIdentitySelectionPolicy.AllowDominantSupport));

        weaker.IdentityEvidence = weaker.IdentityEvidence with { ForwardMeanPixels = .7 };
        Assert.Null(ScanIdentityVerifier.SelectIdentity(
            [weaker, winner], true, true, selectionPolicy: ScanIdentitySelectionPolicy.AllowDominantSupport));
    }

    [Fact]
    public void FailedAlternativePoseDoesNotInvalidateConfirmedIdentity()
    {
        var confirmed = Candidate(ScanIdentityState.Supported, .2);
        var failedPose = new SideEntranceScanCandidate
        {
            Map = confirmed.Map,
            IdentityEvidence = ScanIdentityEvidence.Unverified("alignment-not-confirmed")
        };
        var identity = confirmed.WithHypotheses([confirmed, failedPose]);
        Assert.Equal(confirmed.Map.Id, ScanIdentityVerifier.SelectIdentity([identity], true, true));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([identity], true, false));
        Assert.Null(ScanIdentityVerifier.SelectIdentity(
            [identity, Candidate(ScanIdentityState.Unverified, 0)], true, true));
    }

    [Fact]
    public void AggregateEvidenceDoesNotOverwriteBestSearchHypothesis()
    {
        var seed = Candidate(ScanIdentityState.Excluded, 3);
        var alternate = Candidate(ScanIdentityState.Excluded, 12);
        var candidate = seed.WithHypotheses([seed, alternate]);
        candidate.IdentityEvidence = Candidate(ScanIdentityState.Supported, .2).IdentityEvidence;
        Assert.Equal(ScanIdentityState.Excluded, seed.IdentityEvidence.State);
        Assert.Equal(3d, seed.IdentityEvidence.ForwardMeanPixels);
        Assert.NotSame(candidate, candidate.SearchHypotheses[0]);
    }

    [Fact]
    public void SmallClosedGlyphDoesNotBecomeLongWallByPerimeter()
    {
        Assert.InRange(ScanIdentityVerifier.MeasureStraightConflict(
            [new(0,0), new(12,0), new(12,12), new(0,12)], _ => 20), 11, 13);
        Assert.True(ScanIdentityVerifier.MeasureStraightConflict(
            [new(0,0), new(60,0), new(60,40), new(0,40)], _ => 20)
            >= ScanIdentityVerifier.MaximumContinuousConflictPixels);
        // A real matching stretch splits the conflict instead of summing both sides.
        var line = Enumerable.Range(0, 61).Concat(Enumerable.Range(0, 61).Reverse())
            .Select(x => new Point(x, 0)).ToArray();
        Assert.True(ScanIdentityVerifier.MeasureStraightConflict(
            line, p => p.X is > 20 and < 40 ? 0 : 20)
            < ScanIdentityVerifier.MaximumContinuousConflictPixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimplificationCannotInventUnobservedConflictPixels(bool prepared)
    {
        var forward = Enumerable.Range(0, 101)
            .Select(y => new Point(y is >= 20 and <= 80 ? 1 : 0, y)).ToArray();
        var contour = forward.Concat(forward.Reverse()).ToArray();
        var observed = contour.ToHashSet();
        var vertices = prepared ? Cv2.ApproxPolyDP(contour, 1.5, true) : null;
        var queried = new List<Point>();
        Assert.Equal(0, ScanIdentityVerifier.MeasureStraightConflict(contour, p =>
        {
            queried.Add(p);
            return observed.Contains(p) ? 0 : 20;
        }, out _, out _, vertices));
        Assert.All(queried, p => Assert.Contains(p, observed));
        Assert.True(ScanIdentityVerifier.MeasureStraightConflict(contour, _ => 20,
            out _, out _, vertices) >= ScanIdentityVerifier.MaximumContinuousConflictPixels);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    public void NearFitSeedCanReachAlignmentButCannotSelectIdentity(ScanPerformanceMode mode)
    {
        var candidate = Candidate(ScanIdentityState.Excluded, 1.673430811834092);
        candidate.IdentityEvidence = new(ScanIdentityState.Excluded, 1969, 1969,
            1.673430811834092, .9476891823260538, 30.002465381932925, "visible-contour-conflict");
        Assert.True(ScanIdentityVerifier.ShouldAttemptStructureRegistration(candidate.IdentityEvidence, mode));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, true));
        Assert.False(ScanIdentityVerifier.ShouldAttemptStructureRegistration(candidate.IdentityEvidence, ScanPerformanceMode.DeepScan));
        Assert.False(ScanIdentityVerifier.ShouldAttemptStructureRegistration(
            candidate.IdentityEvidence with { SupportedFraction = .79 }, mode));
        Assert.False(ScanIdentityVerifier.ShouldAttemptStructureRegistration(
            candidate.IdentityEvidence with { TestedPoints = 79 }, mode));
        Assert.False(ScanIdentityVerifier.ShouldAttemptStructureRegistration(ScanIdentityEvidence.Unverified("deadline"), mode));
        candidate.IdentityEvidence = candidate.IdentityEvidence with { State = ScanIdentityState.Supported, LongestConflictPixels = 0 };
        candidate.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, true));
        candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
        Assert.Equal(candidate.Map.Id, ScanIdentityVerifier.SelectIdentity([candidate], true, true));
    }

    [Fact]
    public void DeclaredVariantsSelectBestVerifiedFitWithoutDemandingUniqueMember()
    {
        var winner = Candidate(ScanIdentityState.Supported, .4406356);
        winner.IdentityEvidence = winner.IdentityEvidence with { SupportedFraction = .9774476 };
        var sibling = Candidate(ScanIdentityState.Supported, .9453689);
        sibling.IdentityEvidence = sibling.IdentityEvidence with { SupportedFraction = .9896524 };
        Guid[][] groups = [[winner.Map.Id, sibling.Map.Id]];
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups));
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
        winner.IdentityEvidence = winner.IdentityEvidence with { SupportedFraction = .995 };
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups));
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
    }

    [Fact]
    public void ExcludedSiblingDoesNotVetoVerifiedMemberOfDeclaredGroup()
    {
        var winner = Candidate(ScanIdentityState.Supported, .23);
        var sibling = Candidate(ScanIdentityState.Excluded, .88);
        sibling.IdentityEvidence = sibling.IdentityEvidence with
        { SupportedFraction = .97, Reason = "visible-contour-conflict", LongestConflictPixels = 30 };
        Guid[][] groups = [[winner.Map.Id, sibling.Map.Id]];
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups));
        // An actually dissimilar sibling still permits a unique supported winner.
        sibling.IdentityEvidence = sibling.IdentityEvidence with { SupportedFraction = .3 };
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups));
    }

    [Fact]
    public void VariantGroupCannotHideUnfinishedOrUnrelatedCompetitor()
    {
        var winner = Candidate(ScanIdentityState.Supported, .2);
        var sibling = Candidate(ScanIdentityState.Supported, .2);
        var other = Candidate(ScanIdentityState.Supported, .25);
        Guid[][] groups = [[winner.Map.Id, sibling.Map.Id]];
        foreach (var policy in Enum.GetValues<ScanIdentitySelectionPolicy>())
        {
            Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, sibling, other], true, true, groups, policy));
            sibling.IdentityEvidence = ScanIdentityEvidence.Unverified("alignment-not-confirmed");
            Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, sibling], true, true, groups, policy));
            sibling.IdentityEvidence = other.IdentityEvidence;
            Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, sibling], false, true, groups, policy));
            Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, sibling], true, false, groups, policy));
        }
    }

    [Fact]
    public void EqualVariantFitsHaveStableChoiceRegardlessOfRetrievalOrder()
    {
        var a = Candidate(ScanIdentityState.Supported, .2);
        var b = Candidate(ScanIdentityState.Supported, .2);
        Guid[][] groups = [[a.Map.Id, b.Map.Id]];
        var expected = ScanIdentityVerifier.SelectIdentity([a, b], true, true, groups);
        Assert.NotNull(expected);
        Assert.Equal(expected, ScanIdentityVerifier.SelectIdentity([b, a], true, true, groups));
    }

    [Fact]
    public void FamilyNeedsOneAlignedMemberAndComparesOutsideSupportByPolicy()
    {
        var aligned = Candidate(ScanIdentityState.Supported, .2);
        var sibling = Candidate(ScanIdentityState.Supported, .1);
        sibling.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Guid[][] groups = [[aligned.Map.Id, sibling.Map.Id]];
        Assert.Equal(aligned.Map.Id, ScanIdentityVerifier.SelectIdentity([aligned, sibling], true, true, groups));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([aligned, sibling], true, true));
        var outsider = Candidate(ScanIdentityState.Supported, 3);
        outsider.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Assert.Null(ScanIdentityVerifier.SelectIdentity([aligned, sibling, outsider], true, true, groups));
        Assert.Equal(aligned.Map.Id, ScanIdentityVerifier.SelectIdentity([aligned, sibling, outsider], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
        outsider.IdentityEvidence = outsider.IdentityEvidence with { ForwardMeanPixels = .3 };
        Assert.Null(ScanIdentityVerifier.SelectIdentity([aligned, sibling, outsider], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
        outsider.IdentityEvidence = outsider.IdentityEvidence with { ForwardMeanPixels = .05 };
        Assert.Null(ScanIdentityVerifier.SelectIdentity([aligned, sibling, outsider], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
        aligned.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Assert.Null(ScanIdentityVerifier.SelectIdentity([aligned, sibling], true, true, groups));
    }

    [Fact]
    public void ScreenshotMapThreeFamilyBeatsFullyComparedButUnalignedMapTwentyOne()
    {
        var winner = Candidate(ScanIdentityState.Supported, .38693497949264544);
        winner.IdentityEvidence = winner.IdentityEvidence with { SupportedFraction = 1 };
        var sibling = Candidate(ScanIdentityState.Supported, .23767232876148223);
        sibling.IdentityEvidence = sibling.IdentityEvidence with { SupportedFraction = 1 };
        sibling.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        var outsider = Candidate(ScanIdentityState.Supported, 3.066247582654614);
        outsider.IdentityEvidence = outsider.IdentityEvidence with { SupportedFraction = .8817086527929902 };
        outsider.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Guid[][] groups = [[winner.Map.Id, sibling.Map.Id]];
        foreach (var candidates in new[] { new[] { winner, sibling, outsider }, new[] { outsider, sibling, winner } })
        {
            Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity(candidates, true, true, groups,
                ScanIdentitySelectionPolicy.AllowDominantSupport));
            Assert.Null(ScanIdentityVerifier.SelectIdentity(candidates, true, true, groups));
        }
        outsider.IdentityEvidence = ScanIdentityEvidence.Unverified("deadline");
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, sibling, outsider], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
    }

    [Fact]
    public void OverlappingFamiliesCannotHideNearTieOutsideEachDeclaredGroup()
    {
        var winner = Candidate(ScanIdentityState.Supported, .2);
        var bridge = Candidate(ScanIdentityState.Supported, .2);
        var outsider = Candidate(ScanIdentityState.Supported, .3);
        bridge.Disposition = outsider.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        Guid[][] groups = [[winner.Map.Id, bridge.Map.Id], [bridge.Map.Id, outsider.Map.Id]];
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, bridge, outsider], true, true, groups,
            ScanIdentitySelectionPolicy.AllowDominantSupport));
    }

    [Fact]
    public void RefinementBoundAssumesEveryUntestedDensePointMatchesPerfectly()
    {
        // 100 sampled points with distance 300 and 20 misses. With 1,000 total
        // points, the best possible full-frame cost is .4, not the sample's 4.
        Assert.False(ScanIdentityVerifier.CannotBeatFitCost(300, 20, 1000, .41));
        Assert.True(ScanIdentityVerifier.CannotBeatFitCost(300, 20, 1000, .39));
        Assert.False(ScanIdentityVerifier.CannotBeatFitCost(300, 20, 1000, double.PositiveInfinity));
        var fullEvidence = new ScanIdentityEvidence(ScanIdentityState.Supported, 1000, 1000,
            .3, .98, 0, "perfect-remaining-points");
        Assert.Equal(.4, ScanIdentityVerifier.FitCost(fullEvidence), 10);
        // More measured distance or misses can never improve the lower bound.
        Assert.True(ScanIdentityVerifier.CannotBeatFitCost(320, 21, 1000, .41));
    }

    [Fact]
    public void OutOfReferencePointsRemainInScoreDenominator()
    {
        using var line = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(line, new(30, 0), new(30, 99), Scalar.White);
        var index = ScanStructureIndex.Get(line);
        Assert.Equal(.5, index.Score([new(30, 20), new(130, 20)], 1, 0, 0), 6);
    }

    [Fact]
    public void PolicyAndSettingsAreIndependentSnapshots()
    {
        var settings = new MapRuntimeSettings();
        Assert.Equal(ScanPerformanceMode.Balanced, settings.ScanPerformanceMode);
        settings.ScanPerformanceMode = ScanPerformanceMode.Quality;
        Assert.Equal(settings.ScanPerformanceMode, settings.Clone().ScanPerformanceMode);
        using var context = ScanExecutionContext.Enter(settings.ScanPerformanceMode);
        settings.ScanPerformanceMode = ScanPerformanceMode.Fast;
        Assert.Equal(256, context.Policy.SparsePoints);
        settings.ScanPerformanceMode = (ScanPerformanceMode)99;
        settings.Normalize();
        Assert.Equal(ScanPerformanceMode.Balanced, settings.ScanPerformanceMode);
        Assert.False(settings.SilentScanEnabled);
        Assert.Equal(ScanPerformanceMode.Balanced,
            System.Text.Json.JsonSerializer.Deserialize<MapRuntimeSettings>("{\"ScanPerformanceMode\":\"invalid\"}")!.ScanPerformanceMode);
    }

    private static SideEntranceScanCandidate Candidate(ScanIdentityState state, double distance) => new()
    {
        Map = new MapRecord { Id = Guid.NewGuid() },
        Disposition = SideEntranceCandidateDisposition.Reliable,
        IdentityEvidence = new(state, 100, 100, distance, .95, 0, "test")
    };
}
