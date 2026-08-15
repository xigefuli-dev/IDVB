using IDVBuff.Lifecycle;

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
        Assert.DoesNotContain("Stable packaging is blocked", script);
        Assert.Contains("ecdsa-feed-sha256-assets", script);
        Assert.Contains("Stable feed must contain exactly one full package", script);
        Assert.Contains("--signParams", script);
        Assert.Contains("feed-envelope.json", script);
        Assert.Contains("signed pointer", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update-channel.txt", script);
        Assert.Contains("installer\\VelopackBootstrap.iss", script);
        Assert.Contains("Build install-location chooser", script);
        Assert.Contains("git -C $repositoryRoot archive --format=zip --output=$archive $Context.SourceCommit", script);
        Assert.Contains("'Infrastructure\\Configuration'", script);
        Assert.Contains("'Directory.Build.targets'", script);
        Assert.Contains("'Tools\\Generate-IDVBBuildVersion.ps1'", script);
        Assert.Contains("\"version\": \"1.2.0\"", toolManifest);
    }

    [Fact]
    public void PublicInstallerLetsTheUserChooseTheVelopackInstallDirectory()
    {
        var bootstrapper = Read("installer", "VelopackBootstrap.iss");
        var lifecycle = Read("Lifecycle", "UpdateLifecycleState.cs");
        var layout = Read("Lifecycle", "VelopackInstallLayout.cs");

        Assert.Contains("DisableDirPage=no", bootstrapper);
        Assert.Contains("--silent --installto \"\"{app}\"\"", bootstrapper);
        Assert.Contains("Uninstallable=no", bootstrapper);
        Assert.Contains("sq.version", layout);
        Assert.DoesNotContain("Path.Combine(localAppData, \"IdentityVisionBridge\")", lifecycle);
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
    public void DevelopmentLauncherDoesNotRedirectToAnInstalledInstance()
    {
        var launcher = Read("Startup_IDVB.cmd");
        var program = Read("Program.cs");
        var lifecycle = Read("Lifecycle", "UpdateLifecycleState.cs");

        Assert.Contains("--isolated-dev-instance", launcher);
        Assert.Contains("--isolated-dev-instance", program);
        Assert.Contains("!isCli && !isIsolatedDevelopmentInstance", program);
        Assert.Contains("--isolated-dev-instance", lifecycle);
        Assert.Contains("VelopackInstallLayout.IsValidLauncherPath", lifecycle);
        Assert.Contains("current", Read("Lifecycle", "VelopackInstallLayout.cs"));
        Assert.Contains("UpdateChannelPreference.TryRead()", Read("Lifecycle", "UpdateChannelPolicy.cs"));
        Assert.Contains("UpdateProtocol.TestChannel", Read("Lifecycle", "UpdateChannelPreference.cs"));
        Assert.Contains("UpdateProtocol.StableChannel", Read("Lifecycle", "UpdateChannelPreference.cs"));
    }

    [Fact]
    public void UpdateChannelFlyoutAvoidsTheCrashingTeachingTipPopupPath()
    {
        var settings = Read("Views", "SettingsPage.cs");
        var mainPage = Read("Views", "MainPage.xaml.cs");

        Assert.Contains("var channelFlyout = new Flyout", settings);
        Assert.Contains("titleButton.Flyout = AppDataPaths.IsTestBuild ? null : channelFlyout", settings);
        Assert.Contains("Placement = FlyoutPlacementMode.Bottom", settings);
        Assert.Contains("UpdateChannelPolicy.Resolve()", settings);
        Assert.Contains("AppDataPaths.IsTestBuild", settings);
        Assert.DoesNotContain("UpdateChannelPreference.IsPreviewEnabled", settings);
        Assert.Contains("Content = enablePreview ? \"加入预览计划\" : \"退出预览计划\"", settings);
        Assert.DoesNotContain("new TeachingTip", settings);
        Assert.Contains("view is not MapListPage and not SettingsPage", mainPage);
    }

    [Fact]
    public void VelopackInstallLayoutRequiresTheRootStubAndCurrentContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "IDVB-Tests", Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        var launcher = Path.Combine(root, "IDVB.exe");

        try
        {
            Directory.CreateDirectory(current);
            File.WriteAllText(launcher, string.Empty);
            File.WriteAllText(Path.Combine(root, "Update.exe"), string.Empty);
            File.WriteAllText(Path.Combine(current, "IDVB.exe"), string.Empty);
            File.WriteAllText(Path.Combine(current, "sq.version"), "1.0.0");

            Assert.True(VelopackInstallLayout.IsValidLauncherPath(launcher));

            foreach (var requiredPath in new[]
                     {
                         Path.Combine(root, "Update.exe"),
                         Path.Combine(current, "IDVB.exe"),
                         Path.Combine(current, "sq.version")
                     })
            {
                var contents = File.ReadAllText(requiredPath);
                File.Delete(requiredPath);
                Assert.False(VelopackInstallLayout.IsValidLauncherPath(launcher));
                File.WriteAllText(requiredPath, contents);
            }

            File.Delete(Path.Combine(current, "sq.version"));
            File.WriteAllText(Path.Combine(root, "sq.version"), "1.0.0");
            Assert.False(VelopackInstallLayout.IsValidLauncherPath(launcher));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstalledMainApplicationStartsAThrottledBackgroundUpdateCheck()
    {
        var app = Read("App.xaml.cs");
        var launcher = Read("Lifecycle", "AutomaticUpdateLauncher.cs");

        Assert.Contains("AutomaticUpdateLauncher.TryLaunch()", app);
        Assert.Contains("TimeSpan.FromHours(24)", launcher);
        Assert.Contains("state.Channel, channel", launcher);
        Assert.Contains("Updater", launcher);
        Assert.Contains("IDVB.Updater.exe", launcher);
        Assert.Contains("--background", launcher);
        Assert.Contains("--from-main-pid", launcher);
        Assert.Contains("UpdateChannelPolicy.Resolve()", launcher);
    }

    [Fact]
    public void LegacyInnoBridgeCarriesTheIndependentUpdater()
    {
        var script = Read("installer", "Build-Release.ps1");

        Assert.Contains("Updater\\IDVBuff.Updater.csproj", script);
        Assert.Contains("Updater\\IDVB.Updater.exe", script);
        Assert.Contains("Updater\\UpdateTrust\\idvb-update-2026-01.pem", script);
        Assert.Contains("Invoke-ReleaseSigning -Path (Join-Path $publishDir 'Updater", script);
    }

    [Fact]
    public void IndependentUpdaterDoesNotDependOnWindowsAppRuntime()
    {
        var mainProject = Read("IDVBuff.csproj");
        var project = Read("Updater", "IDVBuff.Updater.csproj");
        var window = Read("Updater", "UpdaterWindow.cs");
        var updateRelease = Read("release", "Invoke-IDVBRelease.ps1");

        Assert.DoesNotContain("<SelfContained>true</SelfContained>", project);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", project);
        Assert.DoesNotContain("Microsoft.WindowsAppSDK", project);
        Assert.Contains("(Join-Path $Context.Source 'Updater\\IDVBuff.Updater.csproj')", updateRelease);
        Assert.Contains("'--self-contained'", updateRelease);
        Assert.Contains("Updater\\IDVBuff.Updater.csproj", mainProject);
        Assert.Contains("AdditionalProperties=\"SelfContained=false\"", mainProject);
        Assert.Contains("CopyUpdaterToApplicationOutput", mainProject);
        Assert.Contains("<RemoveDir Directories=\"$(TargetDir)Updater\"", mainProject);
        Assert.Contains("var layout = new TableLayoutPanel", window);
        Assert.Contains("new RowStyle(SizeType.Percent, 100)", window);
        Assert.Contains("ScrollBars = RichTextBoxScrollBars.Vertical", window);
        Assert.Contains("layout.Controls.Add(buttons, 0, 4)", window);
    }

    [Fact]
    public void EveryReleasePathRequiresTheEmbeddedUpdateTrustRoot()
    {
        var updateRelease = Read("release", "Invoke-IDVBRelease.ps1");
        var githubRelease = Read("installer", "Build-RemoteRelease.ps1");

        Assert.Contains("Updater\\UpdateTrust\\idvb-update-2026-01.pem", updateRelease);
        Assert.Contains("release\\trust\\idvb-update-2026-01.pem", githubRelease);
    }

    [Fact]
    public void UpdateWorkflowSeparatesTestStableAndExternalPublication()
    {
        var workflow = Read("release", "Invoke-IDVBUpdateWorkflow.ps1");
        var releaseRunner = Read("release", "Invoke-IDVBRelease.ps1");

        Assert.Contains("[ValidateSet('Source', 'Test', 'Stable', 'GitHub', 'Audit', 'Status')]", workflow);
        Assert.Contains("[switch]$Publish", workflow);
        Assert.Contains("Invoke-CodeOnlyReleasePreparation", workflow);
        Assert.Contains("Invoke-LocalReleaseCommit", workflow);
        Assert.Contains("git -C $snapshotRoot push origin", workflow);
        Assert.Contains("refs/heads/master", workflow);
        Assert.Contains("Test-PublicCodeOnlyPath", workflow);
        Assert.Contains("Commit this code-only source snapshot and push it to origin/master", workflow);
        Assert.Contains("Code-only source publication is an external GitHub change", workflow);
        Assert.Contains("Invoke-PublicCodeOnlyCommit -PlanOnly", workflow);
        Assert.Contains("The public origin/master code-only snapshot is stale", workflow);
        Assert.Contains("Invoke-Stage 'PublishTest' -DryRun", workflow);
        Assert.Contains("Invoke-Stage 'PublishStable' -DryRun", workflow);
        Assert.Contains("Confirm-OnlineEnvelope", workflow);
        Assert.Contains("Invoke-StageIfPending", workflow);
        Assert.Contains("dotnet build-server shutdown", workflow);
        Assert.Contains("Get-Process -Name dotnet,MSBuild,VBCSCompiler", workflow);
        Assert.Contains("Build hosts are still running", workflow);
        Assert.Contains("continue this release", workflow);
        Assert.Contains("GitHub publication requires a completed stable-channel publication receipt", workflow);
        Assert.Contains("does not match GitHub target", workflow);
        Assert.Contains("Create GitHub Release from stable assets", workflow);
        Assert.DoesNotContain("Build-RemoteRelease.ps1", workflow);
        Assert.Contains("IDVB-Setup-$($manifest.PublicVersion)-x64.exe", workflow);
        Assert.Contains("feed-envelope.json", workflow);
        Assert.DoesNotContain("publish-win-x64-test.json", workflow);
        Assert.DoesNotContain("Remove-Item", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RestartManager", releaseRunner);
        Assert.Contains("Build lock owner:", releaseRunner);
        Assert.Contains("CS2012", releaseRunner);
        Assert.Contains("no owner is visible now", releaseRunner);
        Assert.Contains("A live process is holding a build file", releaseRunner);
        Assert.Contains("Attempting the approved graceful cleanup", releaseRunner);
        Assert.Contains("retry this command", releaseRunner);
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
