using IDVBuff.Modules;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "11")]
public sealed class SafeModeNavigationTests
{
    [Theory]
    [InlineData("home", false)]
    [InlineData("help", false)]
    [InlineData("fundamentals", false)]
    [InlineData("main-settings", false)]
    [InlineData("account", false)]
    [InlineData("map-list", false)]
    [InlineData("tags-templates", false)]
    [InlineData("settings", false)]
    [InlineData("map-status", true)]
    [InlineData("plugins", true)]
    [InlineData("imported-runtime-module", true)]
    public void SafeModeNavigationDoesNotWaitForAnUnbuiltServiceGraph(
        string moduleId, bool usesRestrictedView)
    {
        var unbuiltServices = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var readiness = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: true, isServicesReady: false, unbuiltServices.Task);

        Assert.True(readiness.IsCompletedSuccessfully);
        Assert.False(unbuiltServices.Task.IsCompleted);
        Assert.Equal(usesRestrictedView,
            ModuleNavigationRules.IsSafeModeRestrictedModule(moduleId));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("help")]
    [InlineData("fundamentals")]
    [InlineData("main-settings")]
    [InlineData("account")]
    [InlineData("settings")]
    [InlineData("tags-templates")]
    public void StandaloneNavigationIsAvailableDuringNormalModeStartup(string moduleId)
    {
        var pendingServices = new TaskCompletionSource();

        var readiness = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, pendingServices.Task);

        Assert.True(readiness.IsCompletedSuccessfully);
        Assert.False(pendingServices.Task.IsCompleted);
    }

    [Theory]
    [InlineData("map-list")]
    [InlineData("map-status")]
    [InlineData("plugins")]
    [InlineData("imported-runtime-module")]
    public async Task RuntimeNavigationWaitsForNormalModeServices(string moduleId)
    {
        var pendingServices = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, pendingServices.Task);

        Assert.False(readiness.IsCompleted);
        Assert.Same(pendingServices.Task, readiness);
        pendingServices.SetResult();
        await readiness;
        Assert.True(readiness.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RuntimeNavigationObservesStartupFailure()
    {
        var pendingServices = new TaskCompletionSource();
        var readiness = ModuleNavigationRules.GetRequiredServicesReadyTask(
            "map-list", isSafeMode: false, isServicesReady: false, pendingServices.Task);
        var failure = new InvalidOperationException("Service registration failed.");

        pendingServices.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await readiness));
    }

    [Fact]
    public async Task RuntimeNavigationObservesShutdownCancellation()
    {
        var pendingServices = new TaskCompletionSource();
        var readiness = ModuleNavigationRules.GetRequiredServicesReadyTask(
            "map-list", isSafeMode: false, isServicesReady: false, pendingServices.Task);

        pendingServices.SetCanceled();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readiness);
    }

    [Fact]
    public void ModuleIdsUseTheCatalogsCaseInsensitiveMatching()
    {
        var pendingServices = new TaskCompletionSource();

        Assert.True(ModuleNavigationRules.GetRequiredServicesReadyTask(
            "SETTINGS", isSafeMode: false, isServicesReady: false,
            pendingServices.Task).IsCompletedSuccessfully);
        Assert.False(ModuleNavigationRules.IsSafeModeRestrictedModule("MAP-LIST"));
        Assert.True(ModuleNavigationRules.IsSafeModeRestrictedModule("MAP-STATUS"));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("help")]
    [InlineData("fundamentals")]
    [InlineData("main-settings")]
    [InlineData("account")]
    [InlineData("map-list")]
    [InlineData("tags-templates")]
    [InlineData("settings")]
    [InlineData("map-status")]
    [InlineData("plugins")]
    [InlineData("imported-runtime-module")]
    public async Task SafeModeNavigationRemainsAvailableAfterServiceFailureAndShutdown(string moduleId)
    {
        var failure = new InvalidOperationException("A previous runtime initialization failed.");
        var failedServices = Task.FromException(failure);
        var cancelledServices = Task.FromCanceled(new CancellationToken(canceled: true));

        var afterFailure = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: true, isServicesReady: false, failedServices);
        var afterCancellation = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: true, isServicesReady: false, cancelledServices);

        Assert.True(afterFailure.IsCompletedSuccessfully);
        Assert.True(afterCancellation.IsCompletedSuccessfully);
        await afterFailure;
        await afterCancellation;
        // Bypassing the runtime wait must not consume or modify the failed task.
        Assert.True(failedServices.IsFaulted);
        Assert.Same(failure, failedServices.Exception!.InnerException);
        Assert.True(cancelledServices.IsCanceled);
    }

    [Theory]
    [InlineData("map-list")]
    [InlineData("map-status")]
    [InlineData("plugins")]
    [InlineData("new-runtime-module")]
    public async Task NormalModeRuntimeNavigationCannotBypassAnAlreadyFailedServiceTask(string moduleId)
    {
        var failure = new InvalidOperationException("The service graph could not be built.");
        var failedServices = Task.FromException(failure);
        var cancelledServices = Task.FromCanceled(new CancellationToken(canceled: true));
        var afterFailure = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, failedServices);
        var afterCancellation = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, cancelledServices);

        Assert.Same(failedServices, afterFailure);
        Assert.Same(cancelledServices, afterCancellation);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await afterFailure));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await afterCancellation);
    }

    [Theory]
    [InlineData("map-list")]
    [InlineData("map-status")]
    [InlineData("plugins")]
    [InlineData("new-runtime-module")]
    public async Task ChangingSafeModeDoesNotRetireTheNormalModesOriginalStartupWait(string moduleId)
    {
        var pendingServices = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalNavigation = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, pendingServices.Task);
        var safeNavigation = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: true, isServicesReady: false, pendingServices.Task);
        var normalAgain = ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: false, isServicesReady: false, pendingServices.Task);

        Assert.Same(normalNavigation, normalAgain);
        Assert.False(normalNavigation.IsCompleted);
        Assert.True(safeNavigation.IsCompletedSuccessfully);
        pendingServices.SetResult();
        await normalNavigation;
        await normalAgain;
    }

    [Theory]
    [InlineData("HOME", false)]
    [InlineData("HELP", false)]
    [InlineData("FUNDAMENTALS", false)]
    [InlineData("MAIN-SETTINGS", false)]
    [InlineData("ACCOUNT", false)]
    [InlineData("SETTINGS", false)]
    [InlineData("TAGS-TEMPLATES", false)]
    [InlineData("MAP-LIST", false)]
    [InlineData("MAP-STATUS", true)]
    [InlineData("PLUGINS", true)]
    [InlineData("NEW-RUNTIME-MODULE", true)]
    public void CaseInsensitiveSafeModeRoutingKeepsStandaloneAndRestrictedViewsAvailable(
        string moduleId, bool requiresRestrictedView)
    {
        var pendingServices = new TaskCompletionSource();

        Assert.Equal(requiresRestrictedView, ModuleNavigationRules.IsSafeModeRestrictedModule(moduleId));
        Assert.True(ModuleNavigationRules.GetRequiredServicesReadyTask(
            moduleId, isSafeMode: true, isServicesReady: false, pendingServices.Task).IsCompletedSuccessfully);
        Assert.False(pendingServices.Task.IsCompleted);
    }
}
