namespace IDVB.PluginSystem.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void PluginFoundationProjectsDoNotReferenceWindowsAppSdk()
    {
        var root = FindRepositoryRoot();
        var projects = new[]
        {
            "IDVB.PluginSdk.Abstractions/IDVB.PluginSdk.Abstractions.csproj",
            "IDVB.PluginPackaging/IDVB.PluginPackaging.csproj",
            "IDVB.PluginRuntime/IDVB.PluginRuntime.csproj",
            "IDVBuff.PluginContracts/IDVBuff.PluginContracts.csproj",
            "IDVB.PluginTool/IDVB.PluginTool.csproj",
            "IDVB.PluginTestHost/IDVB.PluginTestHost.csproj",
            "Plugins/Samples/MatchNotifier/IDVB.Sample.MatchNotifier.csproj"
        };

        foreach (var project in projects)
        {
            var text = File.ReadAllText(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("Microsoft.WindowsAppSDK", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IDVBuff.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the IDVB repository root.");
    }
}
