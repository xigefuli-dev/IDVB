using IDVBuff.Cli;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class RealCliCandidateSelectorTests
{
    [Fact]
    public async Task DefaultSelectionUsesHighestConfidenceInsteadOfListPosition()
    {
        using var frame = CreateFrame();
        var candidates = CreateCandidates(0.32d, 0.91d, 0.74d);

        var selected = await new RealCliCandidateSelector(null).SelectAsync(
            frame,
            candidates,
            "test",
            CancellationToken.None);

        Assert.Equal(MapCandidateDecisionKind.SelectKnownMap, selected.Kind);
        Assert.Equal(1, selected.CandidateIndex);
    }

    [Fact]
    public async Task DefaultSelectionPrefersVerifiedCandidateOverHigherScoringReference()
    {
        using var frame = CreateFrame();
        var candidates = new[]
        {
            CreateCandidate(0.95d, isReferenceOnly: true),
            CreateCandidate(0.80d, isReferenceOnly: false),
            CreateCandidate(0.90d, isReferenceOnly: true)
        };

        var selected = await new RealCliCandidateSelector(null).SelectAsync(
            frame,
            candidates,
            "test",
            CancellationToken.None);

        Assert.Equal(MapCandidateDecisionKind.SelectKnownMap, selected.Kind);
        Assert.Equal(1, selected.CandidateIndex);
    }

    [Fact]
    public async Task DefaultSelectionUsesHighestConfidenceWhenAllCandidatesAreReferences()
    {
        using var frame = CreateFrame();
        var candidates = new[]
        {
            CreateCandidate(0.32d, isReferenceOnly: true),
            CreateCandidate(0.91d, isReferenceOnly: true),
            CreateCandidate(0.74d, isReferenceOnly: true)
        };

        var selected = await new RealCliCandidateSelector(null).SelectAsync(
            frame,
            candidates,
            "test",
            CancellationToken.None);

        Assert.Equal(MapCandidateDecisionKind.SelectKnownMap, selected.Kind);
        Assert.Equal(1, selected.CandidateIndex);
    }

    [Fact]
    public async Task DefaultSelectionPreservesVerifiedEvidenceOrder()
    {
        using var frame = CreateFrame();
        var candidates = new[]
        {
            CreateCandidate(0.95d, isReferenceOnly: false, preferredOrder: 1),
            CreateCandidate(0.80d, isReferenceOnly: false, preferredOrder: 0)
        };

        var selected = await new RealCliCandidateSelector(null).SelectAsync(
            frame,
            candidates,
            "test",
            CancellationToken.None);

        Assert.Equal(MapCandidateDecisionKind.SelectKnownMap, selected.Kind);
        Assert.Equal(1, selected.CandidateIndex);
    }

    [Fact]
    public async Task ExplicitPositionIsOneBased()
    {
        using var frame = CreateFrame();
        var candidates = CreateCandidates(0.91d, 0.74d, 0.32d);

        var selected = await new RealCliCandidateSelector(3).SelectAsync(
            frame,
            candidates,
            "test",
            CancellationToken.None);

        Assert.Equal(MapCandidateDecisionKind.SelectKnownMap, selected.Kind);
        Assert.Equal(2, selected.CandidateIndex);
    }

    [Fact]
    public async Task ExplicitPositionOutsideCandidateListFailsClearly()
    {
        using var frame = CreateFrame();
        var candidates = CreateCandidates(0.91d, 0.74d);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new RealCliCandidateSelector(3).SelectAsync(
                frame,
                candidates,
                "test",
                CancellationToken.None));
    }

    private static CapturedGameFrame CreateFrame() => new(
        new Mat(8, 8, MatType.CV_8UC4, Scalar.Black),
        new MapScreenRect(0, 0, 8, 8),
        new MapScreenRect(0, 0, 8, 8),
        IntPtr.Zero);

    private static IReadOnlyList<MapRecognitionChoice> CreateCandidates(
        params double[] confidences) => confidences
        .Select(confidence => CreateCandidate(confidence, isReferenceOnly: false))
        .ToArray();

    private static MapRecognitionChoice CreateCandidate(
        double confidence,
        bool isReferenceOnly,
        int preferredOrder = int.MaxValue) => new()
    {
        Recognition = new RuntimeMapRecognition
        {
            Result = new MapRecognitionResult
            {
                Confidence = confidence
            }
        },
        IsReferenceOnly = isReferenceOnly,
        PreferredOrder = preferredOrder
    };
}
