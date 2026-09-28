using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapHideAlignmentResultBindingTests
{
    [Fact]
    public void Binding_IsOptionalAndClonesIndependently()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.False(settings.HideAlignmentResultBinding.IsConfigured);

        settings.HideAlignmentResultBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x70
        };
        var clone = settings.Clone();
        clone.HideAlignmentResultBinding.VirtualKey = 0x71;

        Assert.Equal(0x70u, settings.HideAlignmentResultBinding.VirtualKey);
        Assert.Equal(0x71u, clone.HideAlignmentResultBinding.VirtualKey);
    }

    [Fact]
    public void Binding_RoundTripsJson()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.HideAlignmentResultBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse,
            MouseButton = MapMouseButton.XButton2
        };

        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(
            JsonSerializer.Serialize(settings))!;
        restored.Normalize();

        Assert.True(restored.HideAlignmentResultBinding.IsConfigured);
        Assert.Equal(
            MapMouseButton.XButton2,
            restored.HideAlignmentResultBinding.MouseButton);
    }

    [Fact]
    public void Binding_RejectsAConflictWithAnExistingAction()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.GameMapToggleBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x48
        };
        settings.HideAlignmentResultBinding =
            settings.GameMapToggleBinding.Clone();

        Assert.Throws<InvalidOperationException>(settings.ValidateInputBindings);

        settings.Normalize();
        Assert.True(settings.GameMapToggleBinding.IsConfigured);
        Assert.False(settings.HideAlignmentResultBinding.IsConfigured);
    }

}
