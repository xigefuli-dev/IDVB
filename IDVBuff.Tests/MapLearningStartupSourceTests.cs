namespace IDVBuff.Tests;

public sealed class MapLearningStartupSourceTests
{
    [Fact]
    public void TraditionalModeDefersTorchModelInitialization()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "SessionOrchestrator.MapLearning.cs"));

        Assert.Contains(
            "if (_settings?.CandidateDecisionMode == MapCandidateDecisionMode.Traditional)",
            source);
        Assert.Contains("await EnsureLearningEngineInitializedAsync", source);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
