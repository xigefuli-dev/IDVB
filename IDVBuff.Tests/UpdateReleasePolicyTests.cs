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
        Assert.Contains("Restore-DeltaBasePackage", script);
        Assert.Contains("Keep-OnlyTargetFeedAssets", script);
        Assert.Contains("Restored signed delta base package", script);
        Assert.Contains("Stable feed must contain exactly one target-version full package", script);
        Assert.Contains("vpk did not produce a target-version delta package", script);
        Assert.Contains("overwrite an immutable release asset", script);
        Assert.Contains("--signParams", script);
        Assert.Contains("feed-envelope.json", script);
        Assert.Contains("signed pointer", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update-channel.txt", script);
        Assert.DoesNotContain("VelopackBootstrap.iss", script);
        Assert.DoesNotContain("Get-InnoCompiler", script);
        Assert.Contains("Copy-Item -LiteralPath $setup.FullName -Destination $versionedSetup", script);
        Assert.Contains("Versioned Setup does not match the native Velopack installer.", script);
        Assert.Contains("git -C $repositoryRoot archive --format=zip --output=$archive $Context.SourceCommit", script);
        Assert.Contains("'Infrastructure\\Configuration'", script);
        Assert.Contains("'Directory.Build.targets'", script);
        Assert.Contains("'Tools\\Generate-IDVBBuildVersion.ps1'", script);
        Assert.Contains("\"version\": \"1.2.0\"", toolManifest);
    }

    [Fact]
    public void PublicInstallerUsesNativeVelopackSetupWithoutInno()
    {
        var releaseRunner = Read("release", "Invoke-IDVBRelease.ps1");
        var lifecycle = Read("Lifecycle", "UpdateLifecycleState.cs");
        var layout = Read("Lifecycle", "VelopackInstallLayout.cs");

        Assert.Contains("'tool', 'run', 'vpk', '--', 'pack'", releaseRunner);
        Assert.Contains("Copy-Item -LiteralPath $setup.FullName -Destination $versionedSetup", releaseRunner);
        Assert.DoesNotContain("VelopackBootstrap.iss", releaseRunner);
        Assert.DoesNotContain("Get-InnoCompiler", releaseRunner);
        Assert.Contains("sq.version", layout);
        Assert.Contains("IsLegacyInnoInstallDirectory(AppContext.BaseDirectory)", lifecycle);
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
        var mainPage = Read("Views", "MainPage.xaml.Part1.cs");

        Assert.Contains("var channelFlyout = new Flyout", settings);
        Assert.Contains("titleButton.Flyout = AppDataPaths.IsTestBuild ? null : channelFlyout", settings);
        Assert.Contains("Placement = FlyoutPlacementMode.Bottom", settings);
        Assert.Contains("UpdateChannelPolicy.Resolve()", settings);
        Assert.Contains("AppDataPaths.IsTestBuild", settings);
        Assert.DoesNotContain("UpdateChannelPreference.IsPreviewEnabled", settings);
        Assert.Contains("Content = enablePreview ? \"加入预览计划\" : \"退出预览计划\"", settings);
        Assert.DoesNotContain("new TeachingTip", settings);
        Assert.Contains("animateMainContent = true", mainPage);
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
        var startupTasks = Read("App.MapSubscriptions.cs");
        var launcher = Read("Lifecycle", "AutomaticUpdateLauncher.cs");

        Assert.Contains("StartStartupBackgroundTasks(session)", app);
        Assert.Contains("Task.Run(AutomaticUpdateLauncher.TryLaunch)", startupTasks);
        Assert.Contains("TimeSpan.FromHours(24)", launcher);
        Assert.Contains("state.Channel, channel", launcher);
        Assert.Contains("Updater", launcher);
        Assert.Contains("IDVB.Updater.exe", launcher);
        Assert.Contains("--background", launcher);
        Assert.Contains("--from-main-pid", launcher);
        Assert.Contains("UpdateChannelPolicy.Resolve()", launcher);
        Assert.Contains("VelopackLocator.Current.CurrentlyInstalledVersion", launcher);
        Assert.Contains("VelopackInstallLayout.IsLegacyInnoInstallDirectory(AppContext.BaseDirectory)", launcher);
        Assert.Contains("unins*.exe", Read("Lifecycle", "VelopackInstallLayout.cs"));
    }

    [Fact]
    public void InstalledUpdatesPreferSignedDeltaPackagesAndExplainFallbacks()
    {
        var source = Read("UpdateCore", "SignedWebUpdateSource.cs");
        var coordinator = Read("Updater", "UpdaterCoordinator.cs");
        var window = Read("Updater", "UpdaterWindow.cs");

        Assert.Contains("delta package outside the signed target version", source);
        Assert.Contains("exactly one full package for the signed target version", source);
        Assert.Contains("DeltasToTarget.Length", coordinator);
        Assert.Contains("仅传输变更内容", window);
        Assert.Contains("自动安全回退到完整包", window);
        Assert.Contains("差分更新", Read("release", "UPDATE_PROCESS.md"));
        Assert.Contains("历史基准 full 包没有被列为待覆盖对象", Read("release", "UPDATE_PROCESS.md"));
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
        Assert.DoesNotContain("AdditionalProperties=\"SelfContained=false\"", mainProject);
        Assert.Contains("<ValidateExecutableReferencesMatchSelfContained>false</ValidateExecutableReferencesMatchSelfContained>", mainProject);
        Assert.Contains("CopyUpdaterToApplicationOutput", mainProject);
        Assert.Contains("<RemoveDir Directories=\"$(TargetDir)Updater\"", mainProject);
        Assert.Contains("var layout = new TableLayoutPanel", window);
        Assert.Contains("new RowStyle(SizeType.Percent, 100)", window);
        Assert.Contains("ScrollBars = RichTextBoxScrollBars.Vertical", window);
        Assert.Contains("layout.Controls.Add(buttons, 0, 4)", window);
    }

    [Fact]
    public void DesktopClientProvidesTrainingSamplesWithoutTorch()
    {
        var project = Read("IDVBuff.csproj");
        var orchestrator = Read("Features", "Maps", "SessionOrchestrator.cs");
        var provider = Read("Features", "Maps", "MapSampleProviderEngine.cs");

        Assert.DoesNotContain("PackageReference Include=\"TorchSharp",
            project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MapLearningPreprocessor.Torch.cs", project);
        Assert.Contains("MapLearningRepository.Models.cs", project);
        Assert.Contains("SiameseMapNetwork.cs", project);
        Assert.Contains("TorchRuntimeConfiguration.cs", project);
        Assert.Contains("new MapSampleProviderEngine()", orchestrator);
        Assert.Contains("SaveHumanSelectionAsync", provider);
        Assert.Contains("_repository.ExportAsync", provider);
        Assert.Contains("SupportsTraining => false", provider);
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
        var codeOnlyPush = Read("release", "Invoke-IDVBCodeOnlyPush.ps1");
        var releaseRunner = Read("release", "Invoke-IDVBRelease.ps1");

        Assert.Contains("[ValidateSet('Source', 'Test', 'Stable', 'GitHub', 'Audit', 'Status')]", workflow);
        Assert.Contains("[switch]$Publish", workflow);
        Assert.Contains("Invoke-CodeOnlyReleasePreparation", workflow);
        Assert.Contains("Invoke-LocalReleaseCommit", workflow);
        Assert.Contains("git -C $snapshotRoot push origin", workflow);
        Assert.Contains("refs/heads/master", workflow);
        Assert.Contains("Test-PublicCodeOnlyPath", workflow);
        Assert.Contains("Startup_IDVB.cmd", workflow);
        Assert.Contains("Startup_RealCLI.cmd", workflow);
        Assert.Contains("Startup_overlay_game.cmd", workflow);
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
        Assert.Contains("GitHub publication requires a completed $channel publication receipt", workflow);
        Assert.Contains("does not match GitHub target", workflow);
        Assert.Contains("Create GitHub Release from $channel assets", workflow);
        Assert.Contains("--prerelease", workflow);
        Assert.Contains("[switch]$Publish", codeOnlyPush);
        Assert.Contains("Test-PublicCodeOnlyPath", codeOnlyPush);
        Assert.Contains("Preview complete. Re-run with -Publish", codeOnlyPush);
        Assert.Contains("git -C $snapshotRoot push origin", codeOnlyPush);
        Assert.Contains("refs/heads/master", codeOnlyPush);
        Assert.DoesNotContain("Build-RemoteRelease.ps1", workflow);
        Assert.Contains("IDVB-Setup-$($manifest.PublicVersion)-x64.exe", workflow);
        Assert.Contains("feed-envelope.json", workflow);
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
    public void CodeOnlyReleasePathsIncludeAllTutorialImages()
    {
        const string guidePngRule = "^Assets/Guide/[A-Za-z0-9._-]+\\.png$";

        foreach (var script in new[]
                 {
                     Read("release", "Invoke-IDVBUpdateWorkflow.ps1"),
                     Read("release", "Invoke-IDVBCodeOnlyPush.ps1"),
                     Read("release", "Invoke-IDVBPublishAction.ps1")
                 })
            Assert.Contains(guidePngRule, script);
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

    [Fact]
    public void ReleasePayloadAndProjectConfigurationPreserveAllEssentialAssets()
    {
        var csproj = Read("IDVBuff.csproj");
        var releaseRunner = Read("release", "Invoke-IDVBRelease.ps1");
        var buildRelease = Read("installer", "Build-Release.ps1");
        var mainPageXaml = Read("Views", "MainPage.xaml");
        var onboardingCs = Read("Views", "MainPage.Onboarding.cs");

        // 1. Ensure all referenced assets in XAML and Onboarding physically exist
        var titleBarIcon = "Assets/Square44x44Logo.targetsize-24_altform-unplated.png";
        Assert.Contains(titleBarIcon, mainPageXaml);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, titleBarIcon.Replace('/', Path.DirectorySeparatorChar))),
            $"Title bar icon '{titleBarIcon}' must exist on disk.");

        var expectedGuideImages = new[]
        {
            "control-panel-end.png",
            "control-panel-start.png",
            "game-map-toggle.png",
            "quick-scan-complete.png",
            "quick-scan-map-open.png",
            "quick-scan-select-map.png",
            "quick-scan-start.png",
            "reset-alignment.png",
            "save-map-cache.png",
            "switch-floor.png"
        };

        var referencedGuideImages = new[]
        {
            "control-panel-end.png",
            "control-panel-start.png",
            "game-map-toggle.png",
            "quick-scan-complete.png",
            "quick-scan-map-open.png",
            "quick-scan-select-map.png",
            "quick-scan-start.png"
        };

        foreach (var guideImage in referencedGuideImages)
            Assert.Contains($"\"{guideImage}\"", onboardingCs);

        foreach (var guideImage in expectedGuideImages)
        {
            var relativePath = Path.Combine("Assets", "Guide", guideImage);
            Assert.True(File.Exists(Path.Combine(RepositoryRoot, relativePath)),
                $"Guide image '{relativePath}' must exist on disk.");
        }

        // 2. Ensure project file copies guide images and title bar icon to output directory
        Assert.Contains(@"<Content Include=""Assets\Guide\*.png"" CopyToOutputDirectory=""PreserveNewest"" />", csproj);
        Assert.Contains(@"<Content Include=""Assets\Square44x44Logo.targetsize-24_altform-unplated.png"" CopyToOutputDirectory=""PreserveNewest"" />", csproj);

        // 3. Ensure release runner payload assertion checks for all essential UI and guide assets
        Assert.Contains(@"'Assets\Square44x44Logo.targetsize-24_altform-unplated.png'", releaseRunner);
        Assert.Contains(@"'Assets\Icons\IDVB_icon_multisize.ico'", releaseRunner);
        Assert.Contains(@"'Assets\Icons\IDVB_icon_square_master.png'", releaseRunner);
        foreach (var guideImage in expectedGuideImages)
        {
            Assert.Contains($@"'Assets\Guide\{guideImage}'", releaseRunner);
        }

        // 4. Ensure legacy installer script also requires them
        Assert.Contains(@"'Assets\Square44x44Logo.targetsize-24_altform-unplated.png'", buildRelease);
        foreach (var guideImage in expectedGuideImages)
        {
            Assert.Contains($@"'Assets\Guide\{guideImage}'", buildRelease);
        }
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
