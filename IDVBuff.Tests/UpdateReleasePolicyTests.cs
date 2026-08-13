namespace IDVBuff.Tests;

public sealed class UpdateReleasePolicyTests
{
    [Fact]
    public void FixedReleaseRunnerIsNonDestructiveAndPinsVelopack()
    {
        var script = Read("release", "Invoke-IDVBRelease.ps1");
        var toolManifest = Read(".config", "dotnet-tools.json");

        Assert.DoesNotContain("Remove-Item", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("r2 object delete", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stable packaging is blocked", script);
        Assert.Contains("AuthenticodeThumbprint", script);
        Assert.Contains("--signParams", script);
        Assert.Contains("feed-envelope.json", script);
        Assert.Contains("signed pointer", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update-channel.txt", script);
        Assert.Contains("\"version\": \"1.2.0\"", toolManifest);
    }

    [Fact]
    public void R2PublicationMovesTheSignedPointerLast()
    {
        var script = Read("release", "Invoke-IDVBRelease.ps1");
        var assets = script.IndexOf("$orderedFiles = @($feed.Assets", StringComparison.Ordinal);
        var installer = script.IndexOf("$orderedFiles += $payload.installer.fileName", StringComparison.Ordinal);
        var feed = script.IndexOf("$orderedFiles += \"releases.$Channel.json\"", StringComparison.Ordinal);
        var envelope = script.IndexOf("$orderedFiles += 'feed-envelope.json'", StringComparison.Ordinal);

        Assert.True(assets >= 0 && assets < installer && installer < feed && feed < envelope);
        Assert.Contains("$R2Bucket/updates/$Channel/$name", script);
    }

    [Fact]
    public void MainRunsVelopackBeforeCreatingWinUiAndKeepsCliMultiInstance()
    {
        var program = Read("Program.cs");
        var velopack = program.IndexOf("VelopackApp.Build()", StringComparison.Ordinal);
        var guiCoordinator = program.IndexOf("new GuiInstanceCoordinator", StringComparison.Ordinal);

        Assert.True(velopack >= 0 && velopack < guiCoordinator);
        Assert.Contains("string.Equals(argument, \"--cli\"", program);
    }

    [Fact]
    public void LegacyInnoBridgeCarriesTheIndependentUpdater()
    {
        var script = Read("installer", "Build-Release.ps1");

        Assert.Contains("Updater\\IDVBuff.Updater.csproj", script);
        Assert.Contains("Updater\\IDVB.Updater.exe", script);
        Assert.Contains("Invoke-ReleaseSigning -Path (Join-Path $publishDir 'Updater", script);
    }

    [Fact]
    public void WorkerExposesOnlyFixedUpdateChannelsAndPreservesRanges()
    {
        var worker = Read("web_installer", "src", "index.js");

        Assert.Contains("win-x64-test|win-x64-stable", worker);
        Assert.Contains("bucket.head(key)", worker);
        Assert.Contains("accept-ranges", worker);
        Assert.Contains("feed-envelope.json", worker);
        Assert.Contains("immutable", worker);
        Assert.Contains("updates/win-x64-stable/", worker);
        Assert.Contains("must never promote a test-channel installer", worker);
    }

    private static string Read(params string[] components) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(components).ToArray()));

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
