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
    public void MatchStateToggleBinding_RejectsConflictsBeforeRegistering()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.GameMapToggleBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard, VirtualKey = 0x73
        };
        settings.MatchStateToggleBinding = settings.GameMapToggleBinding.Clone();
        Assert.Throws<InvalidOperationException>(settings.ValidateInputBindings);
        settings.MatchStateToggleBinding = new MapInputBinding();
        settings.ValidateInputBindings();
    }
}