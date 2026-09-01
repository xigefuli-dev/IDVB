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
