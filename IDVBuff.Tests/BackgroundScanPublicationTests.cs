using System.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class BackgroundScanTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void LateBackgroundIdentityBecomesManualChoiceOnlyWhenEnabled(ScanPerformanceMode mode)
    {
        var identity = CreateIdentity(CreateMap());
        MapRecognitionChoice[] choices = [new() { Recognition = identity, IsReferenceOnly = true }];
        using var execution = ScanExecutionContext.Enter(mode,
            startedTimestamp: Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency);
        execution.CatalogRevision = "catalog-1";
        var offered = BackgroundScanRules.ClassifyAutomaticScan(execution, identity, choices, null,
            "catalog-1", "catalog-1", ScanUncertainAction.ShowCandidates);
        Assert.Equal(BackgroundScanStatus.CompletedAmbiguous, offered.Status);
        Assert.Null(offered.Identity);
        Assert.Same(choices, offered.Choices);
        var failed = BackgroundScanRules.ClassifyAutomaticScan(execution, identity, choices, null,
            "catalog-1", "catalog-1", ScanUncertainAction.ReportUnrecognized);
        Assert.Equal(BackgroundScanStatus.CompletedFailed, failed.Status);
        Assert.Null(failed.Identity);
        Assert.Null(failed.Choices);
    }

    [Theory]
    [InlineData("recognition-catalog")]
    [InlineData("repository-catalog")]
    [InlineData("retrieval-incomplete")]
    [InlineData("superseded")]
    [InlineData("cancelled")]
    public void InvalidBackgroundLeaseCannotPublishIdentityOrManualChoices(string invalidation)
    {
        var identity = CreateIdentity(CreateMap());
        using var cancellation = new CancellationTokenSource();
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced, cancellation.Token,
            isCurrent: () => invalidation != "superseded");
        execution.CatalogRevision = "catalog-1";
        execution.RetrievalCompleted = invalidation != "retrieval-incomplete";
        if (invalidation == "cancelled") cancellation.Cancel();
        var result = BackgroundScanRules.ClassifyAutomaticScan(execution, identity,
            [new() { Recognition = identity, IsReferenceOnly = true }], null,
            invalidation == "recognition-catalog" ? "catalog-2" : "catalog-1",
            invalidation == "repository-catalog" ? "catalog-2" : "catalog-1",
            ScanUncertainAction.ShowCandidates);
        Assert.Equal(BackgroundScanStatus.CompletedFailed, result.Status);
        Assert.Null(result.Identity);
        Assert.Null(result.Choices);
    }

    [Fact]
    public void CurrentBackgroundIdentityStillPublishesWhenCandidateUiIsDisabled()
    {
        var identity = CreateIdentity(CreateMap());
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced);
        execution.CatalogRevision = "catalog-1";
        var result = BackgroundScanRules.ClassifyAutomaticScan(execution, identity, [], null,
            "catalog-1", "catalog-1", ScanUncertainAction.ReportUnrecognized);
        Assert.Equal(BackgroundScanStatus.CompletedIdentified, result.Status);
        Assert.Same(identity, result.Identity);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    [InlineData("catalog")]
    public void PreparedChoicesLosePublicationLeaseWhenTheirContextChanges(string invalidation)
    {
        using var cancellation = new CancellationTokenSource();
        var current = true;
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced, cancellation.Token,
            isCurrent: () => current);
        execution.CatalogRevision = "catalog-1";
        var identity = CreateIdentity(CreateMap());
        var outcome = BackgroundScanRules.ClassifyAutomaticScan(execution, null,
            [new() { Recognition = identity, IsReferenceOnly = true }], null,
            "catalog-1", "catalog-1", ScanUncertainAction.ShowCandidates);
        Assert.Equal(BackgroundScanStatus.CompletedAmbiguous, outcome.Status);
        // Simulate invalidation while asynchronous preview preparation owns the scan gate.
        if (invalidation == "cancelled") cancellation.Cancel();
        if (invalidation == "superseded") current = false;
        Assert.False(BackgroundScanRules.HasCurrentResultContext(execution, "catalog-1",
            invalidation == "catalog" ? "catalog-2" : "catalog-1"));
    }
}
