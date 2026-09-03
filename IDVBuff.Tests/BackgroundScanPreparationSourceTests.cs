namespace IDVBuff.Tests;

public sealed class BackgroundScanPreparationSourceTests
{
    [Fact]
    public void ModelScoreIsFrozenBeforeCompletionIsPublished()
    {
        var root = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "SessionOrchestrator.BackgroundScan.cs"));
        var consumeSource = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.BackgroundScan.Consume.cs"));
        var scoringIndex = preparationSource.IndexOf(
            "_learningEngine.ScoreAsync", StringComparison.Ordinal);
        var completionIndex = preparationSource.LastIndexOf(
            "_backgroundScanStatus = outcome.Status", StringComparison.Ordinal);

        Assert.True(scoringIndex >= 0);
        Assert.True(completionIndex > scoringIndex);
        Assert.Contains("_pendingBackgroundLearningResult", consumeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_learningEngine.ScoreAsync", consumeSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateSelectionDoesNotWaitForLearningSamplePersistence()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.CandidateSelection.cs"));

        Assert.Contains(
            "QueueHumanMapSelectionRecording(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await RecordHumanMapSelectionAsync(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogOnlySelectedMapMustReachFormalStructureRegistration()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.BackgroundScan.Consume.Part1.cs"));

        Assert.Contains(
            "return AlignExactManualFloor(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MapFloorScaleSeedRules.CreateIndependentFloorSeed(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "session: null",
            source,
            StringComparison.Ordinal);

        var alignForStart = source.IndexOf(
            "MapRecognitionAttempt AlignFor(",
            StringComparison.Ordinal);
        var alignForEnd = source.IndexOf(
            "MapFeatureCacheKey? repairCacheKey",
            alignForStart,
            StringComparison.Ordinal);
        Assert.True(alignForStart >= 0);
        Assert.True(alignForEnd > alignForStart);
        var alignForSource = source.Substring(
            alignForStart,
            alignForEnd - alignForStart);
        Assert.DoesNotContain(
            "AlignUsingScaleCache(",
            alignForSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "= Align();",
            alignForSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceScanCandidateMustRebuildSideSeedAndReachFormal()
    {
        var root = FindRepositoryRoot();
        var consumeSource = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.BackgroundScan.Consume.cs"));
        var alignmentSource = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.BackgroundScan.Consume.Part1.cs"));

        Assert.Contains(
            "TryCreateSideEntranceAlignmentSeed(",
            consumeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_pendingBackgroundSeed = rebuiltSeed",
            consumeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AlignSideEntrance(",
            alignmentSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UserConfirmedIdentityMustRemainConfidenceOneDuringFirstAlignment()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.CandidateSelection.cs"));

        Assert.Contains(
            "var identityLock = LockSelectedMapIdentity(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "return new CandidateSelectionResolution(identityLock, false);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IdentityConfidence = 1d",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulBackgroundConsumeIsNotImmediatelyRealigned()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps",
            "SessionOrchestrator.PipelineHelpers.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "else if (IsBackgroundScanCompleted)\n"
                + "            await ConsumeBackgroundScanAsync(toggle);\n"
                + "        else\n",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
