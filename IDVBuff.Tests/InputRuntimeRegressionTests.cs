using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed class InputRuntimeRegressionTests
{
    [Fact]
    public async Task ActivationSavesOnlyAfterListenerInstallation()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var installed = false;
        var saves = new List<bool>();
        await InputRuntimeActivation.ApplyAsync(settings, true,
            () => installed = settings.IsEnabled,
            () =>
            {
                Assert.True(installed);
                saves.Add(settings.IsEnabled);
                return Task.CompletedTask;
            });
        Assert.True(settings.IsEnabled);
        Assert.Equal(new[] { true }, saves);
    }

    [Fact]
    public async Task FailedInstallationPersistsDisabledAndReportsOriginalError()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var failure = new InvalidOperationException("mouse hook failed");
        var cleared = false;
        var saves = new List<bool>();
        var result = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InputRuntimeActivation.ApplyAsync(settings, true,
                () =>
                {
                    if (settings.IsEnabled) throw failure;
                    cleared = true;
                },
                () => { saves.Add(settings.IsEnabled); return Task.CompletedTask; }));
        Assert.Same(failure, result);
        Assert.False(settings.IsEnabled);
        Assert.True(cleared);
        Assert.Equal(new[] { false }, saves);
    }

    [Fact]
    public async Task FailedSaveRemovesInstalledListenerAndRetriesDisabledSave()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var installed = false;
        var saves = new List<bool>();
        await Assert.ThrowsAsync<IOException>(() => InputRuntimeActivation.ApplyAsync(settings, true,
            () => installed = settings.IsEnabled,
            () =>
            {
                saves.Add(settings.IsEnabled);
                if (settings.IsEnabled) throw new IOException("disk unavailable");
                return Task.CompletedTask;
            }));
        Assert.False(installed);
        Assert.False(settings.IsEnabled);
        Assert.Equal(new[] { true, false }, saves);
    }

    [Fact]
    public async Task CleanupFailureStillAttemptsDisabledSaveAndReportsBothErrors()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var saves = new List<bool>();
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            InputRuntimeActivation.ApplyAsync(settings, true,
                () => throw new IOException(settings.IsEnabled ? "install" : "cleanup"),
                () => { saves.Add(settings.IsEnabled); return Task.CompletedTask; }));
        Assert.Equal(new[] { "install", "cleanup" }, failure.InnerExceptions.Select(e => e.Message));
        Assert.Equal(new[] { false }, saves);
        Assert.False(settings.IsEnabled);
    }

    [Fact]
    public async Task FailedActivationOverwritesPreviouslyEnabledConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idvb-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new MapRuntimeSettingsRepository(directory);
            var settings = MapRuntimeSettings.CreateDefault();
            settings.IsEnabled = true;
            await repository.SaveAsync(settings);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InputRuntimeActivation.ApplyAsync(settings, true,
                    () => { if (settings.IsEnabled) throw new InvalidOperationException("hook failed"); },
                    () => repository.SaveAsync(settings)));
            Assert.False((await repository.LoadAsync()).IsEnabled);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RealReleaseRearmsEvenWithinEightyMilliseconds()
    {
        var edges = new KeyboardInputEdges();
        Assert.True(edges.Observe(77, true, false, 1000));
        Assert.True(edges.Observe(77, false, false, 1040));
        Assert.True(edges.Observe(77, true, false, 1080));
        Assert.False(edges.Observe(77, true, true, 1081));
        Assert.False(edges.Observe(77, true, false, 1500));
    }

    [Fact]
    public void PollOnlyPressesAreNotSubjectToATimeWindow()
    {
        var edges = new KeyboardInputEdges();
        Assert.True(edges.Observe(77, true, true, 1000));
        Assert.True(edges.Observe(77, false, true, 1040));
        Assert.True(edges.Observe(77, true, true, 1080));
    }

    [Fact]
    public void OldAsyncStateCannotUndoHookDownOrHookUp()
    {
        var edges = new KeyboardInputEdges();
        Assert.True(edges.Observe(77, true, false, 1000));
        Assert.False(edges.Observe(77, false, true, 1001));
        Assert.False(edges.Observe(77, true, true, 1015));
        Assert.True(edges.Observe(77, false, false, 1040));
        Assert.False(edges.Observe(77, true, true, 1041));
        Assert.False(edges.Observe(77, false, true, 1055));
        Assert.True(edges.Observe(77, true, false, 1080));
    }

    [Fact]
    public void MissingHookReleaseCanStillRecoverThroughPolling()
    {
        var edges = new KeyboardInputEdges();
        Assert.True(edges.Observe(77, true, false, 1000));
        Assert.True(edges.Observe(77, false, true, 1120));
        Assert.True(edges.Observe(77, true, true, 1135));
    }

    [Fact]
    public void HostSyntheticReleaseDoesNotRearmHeldPhysicalKey()
    {
        var edges = new KeyboardInputEdges();
        Assert.True(edges.Observe(77, true, false, 1000));
        edges.IgnoreHostRelease(77);
        Assert.False(edges.Observe(77, false, true, 1015));
        Assert.False(edges.Observe(77, false, true, 1600));
        Assert.False(edges.Observe(77, true, false, 1700));
        Assert.True(edges.Observe(77, false, false, 1800));
        Assert.True(edges.Observe(77, true, false, 1830));
    }

    [Fact]
    public void ReconfigurationClearsAllPendingAndSyntheticState()
    {
        var edges = new KeyboardInputEdges();
        edges.InitializePressed(77);
        edges.IgnoreHostRelease(77);
        edges.Clear();
        Assert.True(edges.Observe(77, true, true, 1000));
        edges.InitializePressed(78);
        Assert.False(edges.Observe(78, true, true, 1001));
        Assert.True(edges.Observe(78, false, true, 1002));
        Assert.True(edges.Observe(78, true, true, 1003));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingBindingsAreRejectedWithoutErasingEitherBinding(bool enabled)
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.IsEnabled = enabled;
        settings.GameMapToggleBinding = Key(77);
        settings.ControlPanelToggleBinding = Key(77);
        Assert.Throws<InvalidOperationException>(settings.ValidateInputBindings);
        Assert.Equal(77u, settings.GameMapToggleBinding.VirtualKey);
        Assert.Equal(77u, settings.ControlPanelToggleBinding.VirtualKey);
        Assert.True(settings.ControlPanelToggleBinding.IsConfigured);
    }

    [Fact]
    public void ValidationKeepsExistingModifierCombinationPolicy()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.GameMapToggleBinding = Key(77);
        settings.ControlPanelToggleBinding = Key(77);
        settings.ControlPanelToggleBinding.Modifiers = MapInputModifiers.Control;
        settings.ValidateInputBindings();
    }

    private static MapInputBinding Key(uint key) => new()
    {
        Kind = MapInputBindingKind.Keyboard,
        VirtualKey = key
    };
}
