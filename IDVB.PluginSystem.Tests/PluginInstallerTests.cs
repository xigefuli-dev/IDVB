using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;

namespace IDVB.PluginSystem.Tests;

public sealed class PluginInstallerTests
{
    [Fact]
    public async Task FailingStartupDiagnosticDoesNotPreventRuntimeStartup()
    {
        using var fixture = new PluginPackageTestFixture();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), developerMode: false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        var contexts = new CountingContextFactory();
        var observations = 0;
        await using var runtime = new ThirdPartyPluginRuntimeManager(directories, state, installer, contexts)
        {
            StartupDiagnostic = _ =>
            {
                observations++;
                throw new IOException("Diagnostic sink unavailable");
            }
        };
        await runtime.SetMatchActivationAsync(false);
        await runtime.StartAsync();
        await runtime.StartAsync();
        Assert.True(observations > 0);
        Assert.Equal(0, contexts.Created);
        Assert.Empty(runtime.Statuses);
    }

    [Fact]
    public async Task FirstInstallBindsPublisherAndRemainsDisabled()
    {
        using var fixture = new PluginPackageTestFixture();
        var packagePath = await fixture.PackAsync(
            fixture.CreateManifest(capabilities: [PluginCapabilityIds.NotificationsPost]));
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), developerMode: false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            packagePath,
            new PluginInstallApproval
            {
                ApprovedCapabilities = new HashSet<string> { PluginCapabilityIds.NotificationsPost }
            }));
        var result = await installer.InstallAsync(
            packagePath,
            new PluginInstallApproval
            {
                TrustPublisher = true,
                ApprovedCapabilities = new HashSet<string> { PluginCapabilityIds.NotificationsPost }
            });

        Assert.False(result.CatalogEntry.Enabled);
        Assert.Equal("1.0.0", result.CatalogEntry.PendingVersion);
        Assert.Single((await state.ReadPublishersAsync()).Publishers);
    }

    [Fact]
    public async Task DisabledPluginDoesNotCreateContextOrLoadCode()
    {
        using var fixture = new PluginPackageTestFixture();
        var packagePath = await fixture.PackAsync();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), developerMode: false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        await installer.InstallAsync(
            packagePath,
            new PluginInstallApproval { TrustPublisher = true });
        var contexts = new CountingContextFactory();
        await using var runtime = new ThirdPartyPluginRuntimeManager(directories, state, installer, contexts);

        await runtime.StartAsync();

        Assert.Equal(0, contexts.Created);
        Assert.DoesNotContain(runtime.Statuses, status => status.State == ThirdPartyPluginState.Running);
    }

    [Fact]
    public async Task ProductionAndDeveloperStateAreIsolated()
    {
        using var fixture = new PluginPackageTestFixture();
        var production = new PluginDirectories(fixture.Root, developerMode: false);
        var developer = new PluginDirectories(fixture.Root, developerMode: true);

        Assert.NotEqual(production.Root, developer.Root);
        Assert.NotEqual(production.CatalogPath, developer.CatalogPath);
        Assert.NotEqual(production.TrustedPublishersPath, developer.TrustedPublishersPath);
    }

    [Fact]
    public async Task EnabledPluginLoadsWithSharedSdkIdentityAndCanBeStopped()
    {
        using var fixture = new PluginPackageTestFixture();
        var packagePath = await fixture.PackAsync();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), developerMode: false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        await installer.InstallAsync(packagePath, new PluginInstallApproval { TrustPublisher = true });
        var capabilitySource = new EmptyCapabilitySource();
        var contextFactory = new DefaultThirdPartyPluginContextFactory(
            capabilitySource,
            _ => new DelegatePluginLogger((_, _, _) => { }));
        var runtime = new ThirdPartyPluginRuntimeManager(
            directories, state, installer, contextFactory);
        await runtime.StartAsync();

        await runtime.SetEnabledAsync("tests.match-notifier", true);

        Assert.Contains(runtime.Statuses, status => status.State == ThirdPartyPluginState.Running);
        await runtime.SetEnabledAsync("tests.match-notifier", false);
        Assert.DoesNotContain(runtime.Statuses, status => status.State == ThirdPartyPluginState.Running);
        await runtime.DisposeAsync();
        runtime = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRemovesPackagesAndHonorsPrivateDataChoice(bool deleteData)
    {
        using var fixture = new PluginPackageTestFixture();
        var packagePath = await fixture.PackAsync();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        await installer.InstallAsync(packagePath, new PluginInstallApproval { TrustPublisher = true });
        await installer.ApplyStartupChangesAsync();
        var dataDirectory = directories.GetDataDirectory("tests.publisher", "tests.match-notifier");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "data.txt"), "preserve unless requested");

        await installer.MarkForUninstallAsync("tests.match-notifier", deleteData);
        await installer.ApplyStartupChangesAsync();

        Assert.Empty((await state.ReadCatalogAsync()).Plugins);
        Assert.False(Directory.Exists(Path.Combine(directories.Packages, "tests.match-notifier")));
        Assert.Equal(!deleteData, Directory.Exists(dataDirectory));
    }

    [Fact]
    public async Task ManagedTaskFailureIsReportedToTheHost()
    {
        using var lifetime = new CancellationTokenSource();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registry = new PluginTaskRegistry(
            lifetime.Token,
            exception => reported.TrySetResult(exception));

        var handle = registry.Run("failure", _ => throw new InvalidOperationException("simulated"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handle.Completion);
        Assert.Equal("simulated", (await reported.Task.WaitAsync(TimeSpan.FromSeconds(2))).Message);
    }

    private sealed class CountingContextFactory : IThirdPartyPluginContextFactory
    {
        public int Created { get; private set; }

        public ValueTask<IPluginContextLease> CreateAsync(
            IdentityVisionBridge.PluginPackaging.IdvpManifest manifest,
            string dataDirectory,
            IReadOnlySet<string> grantedCapabilities,
            CancellationToken pluginLifetime,
            CancellationToken cancellationToken)
        {
            Created++;
            throw new InvalidOperationException("Disabled plugins must not request a context.");
        }
    }

    [Fact]
    public async Task IndependentVisionPluginRunsWithoutAMatchButDisableStillRemovesItsSessionMarker()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync(fixture.CreateManifest(capabilities: [PluginCapabilityIds.VisionScan]));
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "vision-host"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.6.6");
        await installer.InstallAsync(path, new()
        {
            TrustPublisher = true, ApprovedCapabilities = new HashSet<string> { PluginCapabilityIds.VisionScan }
        });
        await installer.ApplyStartupChangesAsync();
        await ExerciseIndependentVisionLifetimeAsync(directories, state, installer);
        // Collect after the helper's async state machine has released plugin locals.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task ExerciseIndependentVisionLifetimeAsync(PluginDirectories directories,
        PluginStateRepository state, IdvpInstaller installer)
    {
        var contexts = new DefaultThirdPartyPluginContextFactory(new EmptyCapabilitySource(),
            _ => new DelegatePluginLogger((_, _, _) => { }));
        await using var runtime = new ThirdPartyPluginRuntimeManager(directories, state, installer, contexts);
        await runtime.SetMatchActivationAsync(false);
        await runtime.StartAsync();
        await runtime.SetEnabledAsync("tests.match-notifier", true);
        Assert.Contains(runtime.Statuses, status => status.State == ThirdPartyPluginState.Running);
        Assert.True(File.Exists(directories.SessionMarkerPath));
        await runtime.SetMatchActivationAsync(true);
        await runtime.SetMatchActivationAsync(false);
        Assert.Contains(runtime.Statuses, status => status.State == ThirdPartyPluginState.Running);
        await runtime.SetEnabledAsync("tests.match-notifier", false);
        Assert.False(File.Exists(directories.SessionMarkerPath));
        await runtime.StopAsync();
    }

    private sealed class EmptyCapabilitySource : IPluginCapabilitySource
    {
        public ValueTask<IReadOnlyDictionary<Type, IPluginCapability>> CreateAsync(
            IdentityVisionBridge.PluginPackaging.IdvpManifest manifest,
            string dataDirectory,
            PluginSettingsService settings,
            IReadOnlySet<string> grantedCapabilities,
            CancellationToken pluginLifetime,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<Type, IPluginCapability>>(
                new Dictionary<Type, IPluginCapability>());
    }
}
