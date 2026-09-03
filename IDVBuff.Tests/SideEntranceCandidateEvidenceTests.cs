using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class SideEntranceCandidateEvidenceTests
{
    [Fact]
    public void StrictInitialIdentityTuningCapsBothChamferLimitsWithoutMutatingSource()
    {
        var source = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 8d,
            RestrictedSearchMaximumChamferPixels = 5d,
            ScaleSearchRadius = 0.04d,
            MinimumEdgeCoverage = 0.68d
        };

        var strict = MapScaleSeedResolver
            .CreateStrictInitialIdentityValidationTuning(source);

        Assert.Equal(3d, strict.MaximumChamferPixels);
        Assert.Equal(3d, strict.RestrictedSearchMaximumChamferPixels);
        Assert.Equal(0.04d, strict.ScaleSearchRadius);
        Assert.Equal(0.68d, strict.MinimumEdgeCoverage);
        Assert.Equal(3d, source.MaximumChamferPixels);
        Assert.Equal(3d, source.RestrictedSearchMaximumChamferPixels);
    }

    [Fact]
    public void StrictStructureAcceptancePromotesAmbiguousLowIdentityCandidate()
    {
        var candidate = new SideEntranceScanCandidate
        {
            Map = new MapRecord { Id = Guid.NewGuid() },
            FloorKey = "1f",
            MatchScore = 0.81d,
            TemplateMargin = 0.001d,
            Disposition = SideEntranceCandidateDisposition.Rejected,
            RejectionReason = SideEntranceRejectionReason.AmbiguousTemplateRanking,
            RejectionDetail = "template margin too small"
        };
        var structure = CreateStructureResult(
            rawChamfer: 1.25d,
            compositeCost: 2.75d,
            edgeCoverage: 0.72d,
            occupancyCoverage: 0.83d,
            candidateMargin: 0.06d);
        var attempt = new MapRecognitionAttempt
        {
            StructureResult = structure,
            StructureAccepted = true,
            Recognition = CreateRecognition(candidate.Map, 0.7996d)
        };

        var promoted = SideEntranceCandidateEvidence.ApplyStructureAttempt(
            candidate,
            attempt);

        Assert.True(promoted);
        Assert.Equal(SideEntranceCandidateDisposition.Reliable,
            candidate.Disposition);
        Assert.Equal(SideEntranceRejectionReason.None,
            candidate.RejectionReason);
        Assert.Empty(candidate.RejectionDetail);
        Assert.Equal(0.7996d, candidate.IdentityConfidence);
        Assert.Equal(1.25d, candidate.RawChamferPixels);
        Assert.Equal(2.75d, candidate.StructureCompositeCost);
        Assert.NotEqual(candidate.RawChamferPixels,
            candidate.StructureCompositeCost);
    }

    [Fact]
    public void VerifiedOrderingUsesStrictGeometryTieBreakSequence()
    {
        var worseChamfer = Candidate(
            rawChamfer: 1.3d,
            edgeCoverage: 0.99d,
            occupancyCoverage: 0.99d,
            candidateMargin: 0.99d,
            templateScore: 0.99d);
        var weakerEdge = Candidate(
            rawChamfer: 1.2d,
            edgeCoverage: 0.80d,
            occupancyCoverage: 0.99d,
            candidateMargin: 0.99d,
            templateScore: 0.99d);
        var weakerOccupancy = Candidate(
            rawChamfer: 1.2d,
            edgeCoverage: 0.81d,
            occupancyCoverage: 0.80d,
            candidateMargin: 0.99d,
            templateScore: 0.99d);
        var weakerMargin = Candidate(
            rawChamfer: 1.2d,
            edgeCoverage: 0.81d,
            occupancyCoverage: 0.81d,
            candidateMargin: 0.05d,
            templateScore: 0.99d);
        var weakerTemplate = Candidate(
            rawChamfer: 1.2d,
            edgeCoverage: 0.81d,
            occupancyCoverage: 0.81d,
            candidateMargin: 0.06d,
            templateScore: 0.70d);
        var strongest = Candidate(
            rawChamfer: 1.2d,
            edgeCoverage: 0.81d,
            occupancyCoverage: 0.81d,
            candidateMargin: 0.06d,
            templateScore: 0.71d);

        var ordered = SideEntranceCandidateEvidence.OrderVerified(
            [worseChamfer, weakerEdge, weakerOccupancy, weakerMargin,
                weakerTemplate, strongest],
            candidate => candidate).ToArray();

        Assert.Equal(
            [strongest, weakerTemplate, weakerMargin, weakerOccupancy,
                weakerEdge, worseChamfer],
            ordered);
    }

    [Fact]
    public void StructureMetricLogSeparatesRawChamferFromCompositeCost()
    {
        var structure = CreateStructureResult(
            rawChamfer: 1.55d,
            compositeCost: 2.49d,
            edgeCoverage: 0.75d,
            occupancyCoverage: 0.82d,
            candidateMargin: 0.07d,
            usedRestrictedSearch: true);

        var details = SideEntranceCandidateEvidence
            .BuildStructureMetricLogDetails(structure, 3d);

        Assert.Equal(1.55d, Assert.IsType<double>(
            details["rawChamferPixels"]));
        Assert.Equal(2.49d, Assert.IsType<double>(
            details["compositeCost"]));
        Assert.Equal(true, details["usedRestrictedSearch"]);
        Assert.Equal(3d, details["effectiveChamferLimit"]);
        Assert.False(details.ContainsKey("chamfer"));
    }

    [Fact]
    public void PromotionDefensivelyRejectsAcceptedResultAboveStrictChamferLimit()
    {
        var candidate = new SideEntranceScanCandidate
        {
            Map = new MapRecord { Id = Guid.NewGuid() },
            FloorKey = "1f",
            MatchScore = 0.95d
        };
        var attempt = new MapRecognitionAttempt
        {
            StructureResult = CreateStructureResult(
                rawChamfer: 3.01d,
                compositeCost: 1d,
                edgeCoverage: 0.95d,
                occupancyCoverage: 0.95d,
                candidateMargin: 0.20d),
            StructureAccepted = true,
            Recognition = CreateRecognition(candidate.Map, 0.99d)
        };

        var promoted = SideEntranceCandidateEvidence.ApplyStructureAttempt(
            candidate,
            attempt);

        Assert.False(promoted);
        Assert.Equal(
            SideEntranceCandidateDisposition.NeedsVerification,
            candidate.Disposition);
        Assert.Equal(
            SideEntranceRejectionReason.StructureRejected,
            candidate.RejectionReason);
        Assert.Contains("3.0px", candidate.RejectionDetail);
    }

    [Fact]
    public void VerificationSelectionKeepsOnlyTheTopThreeNearTies()
    {
        var candidates = Enumerable.Range(0, 12)
            .Select(index => new SideEntranceScanCandidate
            {
                Map = new MapRecord
                {
                    Id = Guid.NewGuid(),
                    SequenceNumber = index + 1
                },
                FloorKey = "1f",
                MatchScore = 0.90d - (index * 0.01d)
            })
            .ToArray();

        var selected = SideEntranceCandidateEvidence
            .SelectVerificationCandidates(candidates);

        Assert.Equal(3, selected.Count);
        Assert.Equal(
            new[] { 1, 2, 3 },
            selected.Select(candidate => candidate.Map.SequenceNumber));
    }

    [Theory]
    [InlineData(0.79d, 1)]
    [InlineData(0.84d, 2)]
    [InlineData(0.86d, 3)]
    public void VerificationSelectionUsesTopScoreMargin(
        double secondScore,
        int expectedCount)
    {
        var candidates = new[]
        {
            new SideEntranceScanCandidate
            {
                Map = new MapRecord { Id = Guid.NewGuid(), SequenceNumber = 1 },
                FloorKey = "1f",
                MatchScore = 0.90d
            },
            new SideEntranceScanCandidate
            {
                Map = new MapRecord { Id = Guid.NewGuid(), SequenceNumber = 2 },
                FloorKey = "1f",
                MatchScore = secondScore
            },
            new SideEntranceScanCandidate
            {
                Map = new MapRecord { Id = Guid.NewGuid(), SequenceNumber = 3 },
                FloorKey = "1f",
                MatchScore = 0.70d
            }
        };

        var selected = SideEntranceCandidateEvidence
            .SelectVerificationCandidates(candidates);

        Assert.Equal(expectedCount, selected.Count);
        Assert.Equal(0.90d, selected[0].MatchScore);
    }

    private static SideEntranceScanCandidate Candidate(
        double rawChamfer,
        double edgeCoverage,
        double occupancyCoverage,
        double candidateMargin,
        double templateScore) => new()
    {
        Map = new MapRecord { Id = Guid.NewGuid() },
        FloorKey = "1f",
        MatchScore = templateScore,
        RawChamferPixels = rawChamfer,
        StructureEdgeCoverage = edgeCoverage,
        StructureOccupancyCoverage = occupancyCoverage,
        StructureCandidateMargin = candidateMargin,
        Disposition = SideEntranceCandidateDisposition.Reliable
    };

    private static RuntimeMapRecognition CreateRecognition(
        MapRecord map,
        double identityConfidence) => new()
    {
        Map = map,
        Result = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "1f",
            Confidence = 0.91d,
            IdentityConfidence = identityConfidence,
            LocalizationConfidence = 0.91d
        }
    };

    private static MapStructureRegistrationResult CreateStructureResult(
        double rawChamfer,
        double compositeCost,
        double edgeCoverage,
        double occupancyCoverage,
        double candidateMargin,
        bool usedRestrictedSearch = true) => new()
    {
        Accepted = true,
        Confidence = 0.91d,
        BestScore = compositeCost,
        CandidateMargin = candidateMargin,
        UsedRestrictedSearch = usedRestrictedSearch,
        ConfidenceBreakdown = new MapStructureConfidenceBreakdown
        {
            ChamferPixels = rawChamfer,
            EdgeCoverage = edgeCoverage,
            OccupancyCoverage = occupancyCoverage
        },
        Candidates =
        [
            new MapStructureCandidate
            {
                ChamferPixels = rawChamfer,
                CompositeCost = compositeCost,
                EdgeCoverage = edgeCoverage,
                OccupancyCoverage = occupancyCoverage
            }
        ]
    };
}
