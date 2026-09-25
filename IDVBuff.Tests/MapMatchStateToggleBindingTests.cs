using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapMatchStateToggleBindingTests
{
    [Fact]
    public void MatchStateToggleBinding_IsUnconfiguredByDefault()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        Assert.False(settings.MatchStateToggleBinding.IsConfigured);
    }

    [Fact]
    public void MatchStateToggleBinding_ClonesIndependently()
    {
        var settings = new MapRuntimeSettings
        {
            MatchStateToggleBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Keyboard,
                VirtualKey = 0x73 // F4
            }
        };

        var clone = settings.Clone();
        clone.MatchStateToggleBinding.VirtualKey = 0x74; // F5

        Assert.Equal(0x73u, settings.MatchStateToggleBinding.VirtualKey);
        Assert.Equal(0x74u, clone.MatchStateToggleBinding.VirtualKey);
    }

    [Fact]
    public void MatchStateToggleBinding_RoundTripsJson()
    {
        var settings = new MapRuntimeSettings
        {
            MatchStateToggleBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Keyboard,
                VirtualKey = 0x73
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.True(restored.MatchStateToggleBinding.IsConfigured);
        Assert.Equal(0x73u, restored.MatchStateToggleBinding.VirtualKey);
    }

    [Fact]
    public void MatchStateToggleBinding_ClearsWhenConflictingWithExistingBinding()
    {
        var sharedKey = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x48 // H
        };

        var settings = new MapRuntimeSettings
        {
            RestMapDisplayBinding = sharedKey.Clone(),
            MatchStateToggleBinding = sharedKey.Clone()
        };

        settings.Normalize();

        Assert.True(settings.RestMapDisplayBinding.IsConfigured);
        Assert.False(settings.MatchStateToggleBinding.IsConfigured);
    }

    [Fact]
    public void MatchStateToggleBinding_IsOptionalForMasterSwitch()
    {
        // 验证只配置 6 项必选按键时，总开关前置校验不受 MatchStateToggleBinding 影响
        var settings = new MapRuntimeSettings
        {
            GameMapToggleBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x31 },
            ControlPanelToggleBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x35 },
            QuickScanBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x72 },
            SwitchFloorBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x36 },
            SaveMapCacheBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x47 },
            RestMapDisplayBinding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x48 }
        };

        Assert.False(settings.MatchStateToggleBinding.IsConfigured);

        var root = FindRepositoryRoot();
        var settingsSource = File.ReadAllText(Path.Combine(root, "Features", "Maps", "SessionOrchestrator.Settings.cs"));
        Assert.DoesNotContain("MatchStateToggleBinding.IsConfigured", settingsSource);
    }

    [Fact]
    public void MatchStateToggle_TriggersAppropriateOverlayNotifications()
    {
        var root = FindRepositoryRoot();
        var operationsSource = File.ReadAllText(Path.Combine(root, "Features", "Maps", "SessionOrchestrator.Operations.cs"));

        // 验证进入对局使用绿色通知（Notice），退出对局使用橙黄色通知（Warning）
        Assert.Contains("OverlayNotificationCenter.Notice", operationsSource);
        Assert.Contains("OverlayNotificationCenter.Warning", operationsSource);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.slnx")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find repository root containing IDVBuff.slnx");
    }
}
