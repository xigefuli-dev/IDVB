namespace IDVBuff.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void ReleaseBuildRejectsUserLocalStateFromThePublishPayload()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "Build-Release.ps1"));

        Assert.Contains("Assert-PublishContainsNoUserData", script);
        Assert.Contains("recognition-statistics.json", script);
        Assert.Contains("settings.json", script);
        Assert.Contains("AlignmentResearch", script);
        Assert.Contains("Assert-PublishContainsNoUserData -PublishDirectory $publishDir", script);
        Assert.Contains("BaseOutputPath", script);
    }

    [Fact]
    public void InnoUninstallerKeepsLocalDataUnlessTheUserExplicitlyDeletesIt()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "IDVB.iss"));

        Assert.Contains("KeepPersonalData := True", script);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON1", script);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON2", script);
        Assert.Contains("Name: \"{localappdata}\\IDVB\"; Check: ShouldRemovePersonalData", script);
        Assert.Contains("if UninstallSilent then", script);
    }

    private static string RepositoryRoot
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         new DirectoryInfo(Directory.GetCurrentDirectory()),
                         new DirectoryInfo(AppContext.BaseDirectory)
                     })
            {
                for (var current = candidate; current is not null; current = current.Parent)
                {
                    if (File.Exists(Path.Combine(current.FullName, "IDVBuff.slnx")))
                        return current.FullName;
                }
            }

            throw new DirectoryNotFoundException("Cannot find the repository root.");
        }
    }
}
