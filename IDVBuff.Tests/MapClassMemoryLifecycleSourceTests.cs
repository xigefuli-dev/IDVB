namespace IDVBuff.Tests;

public sealed class MapClassMemoryLifecycleSourceTests
{
    [Fact]
    public void CatalogRefreshInvalidatesNativeAndModelReferenceCaches()
    {
        var root = FindRepositoryRoot();
        var recognition = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "MapCvRecognitionService.cs"));
        var orchestrator = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "SessionOrchestrator.Operations.cs"));

        Assert.Contains("_structureCache.InvalidateMaps(cache.ChangedMapIds)", recognition);
        Assert.Contains("InvalidateReferenceCacheAsync", orchestrator);
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
