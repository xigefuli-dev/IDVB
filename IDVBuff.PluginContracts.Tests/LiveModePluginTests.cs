using IDVBuff.Core.Contracts;
using IDVBuff.Plugins.LiveMode;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class LiveModePluginTests
{
    [Fact]
    public void DefaultsAreIndependentAndDisplayLayerIsHidden()
    {
        var plugin = new LiveModePlugin();

        Assert.False((bool)plugin.GetSettingValue(LiveModePlugin.HideMainProgramKey)!);
        Assert.True((bool)plugin.GetSettingValue(LiveModePlugin.HideDisplayLayerKey)!);
        Assert.Equal(
            [LiveModePlugin.HideMainProgramKey, LiveModePlugin.HideDisplayLayerKey],
            plugin.Settings.Select(setting => setting.Key).ToArray());
    }

    [Fact]
    public void EnableAndDisableApplyTwoIndependentPolicies()
    {
        var service = new FakeCaptureProtectionService();
        var plugin = new LiveModePlugin();
        plugin.OnLoad(new FakeContext(plugin, new FakeLogger(), service));

        plugin.OnEnable();
        Assert.Equal((true, false, true), service.Policy);

        plugin.SetSettingValue(LiveModePlugin.HideMainProgramKey, true);
        plugin.SetSettingValue(LiveModePlugin.HideDisplayLayerKey, false);
        Assert.Equal((true, true, false), service.Policy);

        plugin.OnDisable();
        Assert.Equal((false, false, false), service.Policy);
    }

    [Fact]
    public void ExistingAndNewWindowsInheritCurrentPolicy()
    {
        var service = new FakeCaptureProtectionService();
        var plugin = new LiveModePlugin();
        plugin.OnLoad(new FakeContext(plugin, new FakeLogger(), service));
        using var main = service.RegisterWindow(
            new IntPtr(1), CaptureProtectionWindowCategory.MainProgram, "main");
        using var overlay = service.RegisterWindow(
            new IntPtr(2), CaptureProtectionWindowCategory.DisplayLayer, "overlay");

        plugin.OnEnable();
        Assert.False(main.IsProtectionApplied);
        Assert.True(overlay.IsProtectionApplied);

        plugin.SetSettingValue(LiveModePlugin.HideMainProgramKey, true);
        Assert.True(main.IsProtectionApplied);
        Assert.True(overlay.IsProtectionApplied);

        plugin.SetSettingValue(LiveModePlugin.HideDisplayLayerKey, false);
        Assert.False(overlay.IsProtectionApplied);
        using var later = service.RegisterWindow(
            new IntPtr(3), CaptureProtectionWindowCategory.DisplayLayer, "later");
        Assert.False(later.IsProtectionApplied);
    }

    private sealed class FakeCaptureProtectionService : ICaptureProtectionService
    {
        private readonly List<FakeRegistration> _registrations = [];
        private bool _disposed;

        public (bool Enabled, bool HideMain, bool HideDisplay) Policy { get; private set; }

        public bool IsPluginEnabled => Policy.Enabled;

        public bool IsProtectionRequested(CaptureProtectionWindowCategory category) =>
            Policy.Enabled && (category == CaptureProtectionWindowCategory.MainProgram
                ? Policy.HideMain
                : Policy.HideDisplay);

        public void SetPolicy(bool pluginEnabled, bool hideMainProgram, bool hideDisplayLayer)
        {
            Policy = (pluginEnabled, hideMainProgram, hideDisplayLayer);
            foreach (var registration in _registrations)
                registration.IsProtectionApplied = IsProtectionRequested(registration.Category);
        }

        public ICaptureProtectionRegistration RegisterWindow(
            IntPtr handle,
            CaptureProtectionWindowCategory category,
            string name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var registration = new FakeRegistration(this, handle, category, name)
            {
                IsProtectionApplied = IsProtectionRequested(category)
            };
            _registrations.Add(registration);
            return registration;
        }

        public void RefreshPolicy() => SetPolicy(
            Policy.Enabled, Policy.HideMain, Policy.HideDisplay);

        public void Dispose()
        {
            _disposed = true;
            _registrations.Clear();
        }

        private sealed class FakeRegistration : ICaptureProtectionRegistration
        {
            private readonly FakeCaptureProtectionService _owner;
            private bool _disposed;

            public FakeRegistration(
                FakeCaptureProtectionService owner,
                IntPtr handle,
                CaptureProtectionWindowCategory category,
                string name)
            {
                _owner = owner;
                Handle = handle;
                Category = category;
                Name = name;
            }

            public IntPtr Handle { get; }
            public CaptureProtectionWindowCategory Category { get; }
            public string Name { get; }
            public bool IsProtectionApplied { get; set; }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _owner._registrations.Remove(this);
            }
        }
    }
}
