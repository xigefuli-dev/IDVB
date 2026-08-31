using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

/// <summary>
/// 插件设置描述符与 Provider 的框架无关契约：宿主 TeachingTip 管理器依赖
/// 这些类型约定（toggle→bool / slider→double / choice→string）渲染设置页。
/// </summary>
public class PluginSettingsContractTests
{
    [Fact]
    public void ToggleSetting_CarriesKeyNameAndDefault()
    {
        var setting = new PluginToggleSetting
        {
            Key = "enabled",
            DisplayName = "启用",
            DefaultValue = true
        };

        Assert.Equal("enabled", setting.Key);
        Assert.Equal("启用", setting.DisplayName);
        Assert.True(setting.DefaultValue);
    }

    [Fact]
    public void SliderSetting_CarriesBoundsAndDefaultsToStepOne()
    {
        var setting = new PluginSliderSetting
        {
            Key = "delay",
            DisplayName = "延迟",
            Minimum = 1,
            Maximum = 50,
            DefaultValue = 5
        };

        Assert.Equal(1, setting.Minimum);
        Assert.Equal(50, setting.Maximum);
        Assert.Equal(5, setting.DefaultValue);
        Assert.Equal(1, setting.StepFrequency);
    }

    [Fact]
    public void ChoiceSetting_DefaultValue_ResolvesByIndex()
    {
        var setting = new PluginChoiceSetting
        {
            Key = "mode",
            DisplayName = "模式",
            Options = ["A", "B", "C"],
            DefaultIndex = 1
        };

        Assert.Equal("B", setting.DefaultValue);
    }

    [Fact]
    public void ChoiceSetting_DefaultValue_OutOfRangeFallsBackToFirstOption()
    {
        var setting = new PluginChoiceSetting
        {
            Key = "mode",
            DisplayName = "模式",
            Options = ["A", "B", "C"],
            DefaultIndex = 99
        };

        Assert.Equal("A", setting.DefaultValue);
    }

    [Fact]
    public void ChoiceSetting_DefaultValue_EmptyOptionsThrows()
    {
        var setting = new PluginChoiceSetting
        {
            Key = "mode",
            DisplayName = "模式",
            Options = [],
            DefaultIndex = 0
        };

        Assert.Throws<InvalidOperationException>(() => _ = setting.DefaultValue);
    }

    [Fact]
    public void PluginInputBinding_RoundTripsKeyboardCombination()
    {
        var original = PluginInputBinding.Keyboard(
            0x41,
            PluginInputModifiers.Control | PluginInputModifiers.Shift);

        Assert.True(PluginInputBinding.TryParse(
            original.StorageValue,
            out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal("Ctrl + Shift + A", parsed.DisplayName);
    }

    [Fact]
    public void PluginInputBinding_RoundTripsOrdinaryKeyCombination()
    {
        var original = PluginInputBinding.Keyboard(0x43,
            companionVirtualKeys: [0x42]);

        Assert.True(PluginInputBinding.TryParse(original.StorageValue, out var parsed));
        Assert.Equal("B + C", parsed.DisplayName);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void PluginInputBinding_ParsesMouseAndNone()
    {
        Assert.True(PluginInputBinding.TryParse(
            "mouse:1",
            PluginInputBindingKinds.Mouse,
            out var mouse));
        Assert.Equal(PluginMouseButton.Right, mouse.MouseButton);

        Assert.True(PluginInputBinding.TryParse(
            "none",
            PluginInputBindingKinds.Keyboard,
            out var none));
        Assert.False(none.IsConfigured);
    }

    [Theory]
    [InlineData(0xC0u, "`")]
    [InlineData(0x60u, "小键盘 0")]
    [InlineData(0xBAu, ";")]
    [InlineData(0xA6u, "浏览器后退")]
    public void PluginInputBinding_UsesReadableNamesForNonAlphanumericKeys(
        uint virtualKey,
        string expectedName)
    {
        var binding = PluginInputBinding.Keyboard(virtualKey);

        Assert.Equal(expectedName, binding.DisplayName);
        Assert.DoesNotContain("VK", binding.DisplayName,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_GetSettingValue_ReturnsValuesPerContract()
    {
        var provider = new SettingsPlugin();

        Assert.True((bool)provider.GetSettingValue("toggle")!);
        Assert.Equal(5.0, provider.GetSettingValue("slider"));
        Assert.Equal("A", provider.GetSettingValue("choice"));
        Assert.Null(provider.GetSettingValue("unknown"));
    }

    [Fact]
    public void Provider_SetSettingValue_UpdatesAndPersistsInMemory()
    {
        var provider = new SettingsPlugin();

        provider.SetSettingValue("toggle", false);
        provider.SetSettingValue("slider", 42.0);
        provider.SetSettingValue("choice", "C");

        Assert.False((bool)provider.GetSettingValue("toggle")!);
        Assert.Equal(42.0, provider.GetSettingValue("slider"));
        Assert.Equal("C", provider.GetSettingValue("choice"));
    }

    [Fact]
    public void Provider_Settings_ExposesDescriptorsInUiOrder()
    {
        var provider = new SettingsPlugin();

        Assert.Equal(3, provider.Settings.Count);
        Assert.Equal(["toggle", "slider", "choice"],
            provider.Settings.Select(setting => setting.Key).ToArray());
    }

    [Fact]
    public void InventoryScale_StoresSeparate16By9And16By10CoordinateLists()
    {
        Assert.Equal(10, PluginInventoryScale.AspectRatio16By9.Count);
        Assert.Equal(10, PluginInventoryScale.AspectRatio16By10.Count);

        Assert.Equal(
            [
                new PluginInventoryCoordinate(1, 0.22, 0.59),
                new PluginInventoryCoordinate(1, 0.30, 0.59),
                new PluginInventoryCoordinate(1, 0.37, 0.59),
                new PluginInventoryCoordinate(2, 0.22, 0.72),
                new PluginInventoryCoordinate(2, 0.29, 0.72),
                new PluginInventoryCoordinate(2, 0.36, 0.72),
                new PluginInventoryCoordinate(3, 0.39, 0.92),
                new PluginInventoryCoordinate(3, 0.47, 0.92),
                new PluginInventoryCoordinate(3, 0.56, 0.92),
                new PluginInventoryCoordinate(3, 0.64, 0.92)
            ],
            PluginInventoryScale.AspectRatio16By9);

        Assert.Equal(
            [
                new PluginInventoryCoordinate(1, 0.22, 0.64),
                new PluginInventoryCoordinate(1, 0.29, 0.64),
                new PluginInventoryCoordinate(1, 0.37, 0.64),
                new PluginInventoryCoordinate(2, 0.22, 0.74),
                new PluginInventoryCoordinate(2, 0.30, 0.74),
                new PluginInventoryCoordinate(2, 0.37, 0.74),
                new PluginInventoryCoordinate(3, 0.39, 0.93),
                new PluginInventoryCoordinate(3, 0.47, 0.93),
                new PluginInventoryCoordinate(3, 0.55, 0.93),
                new PluginInventoryCoordinate(3, 0.64, 0.93)
            ],
            PluginInventoryScale.AspectRatio16By10);

        Assert.NotSame(
            PluginInventoryScale.AspectRatio16By9,
            PluginInventoryScale.AspectRatio16By10);
    }

    /// <summary>实现 IPluginSettingsProvider 契约的假插件，验证宿主依赖的类型约定。</summary>
    private sealed class SettingsPlugin : PluginBase, IPluginSettingsProvider
    {
        private bool _toggle = true;
        private double _slider = 5;
        private string _choice = "A";

        public override string Id => "settings";

        public IReadOnlyList<IPluginSetting> Settings { get; } =
        [
            new PluginToggleSetting
            {
                Key = "toggle",
                DisplayName = "开关",
                DefaultValue = true
            },
            new PluginSliderSetting
            {
                Key = "slider",
                DisplayName = "滑条",
                Minimum = 1,
                Maximum = 50,
                DefaultValue = 5
            },
            new PluginChoiceSetting
            {
                Key = "choice",
                DisplayName = "下拉",
                Options = ["A", "B", "C"],
                DefaultIndex = 0
            }
        ];

        public object? GetSettingValue(string key) => key switch
        {
            "toggle" => _toggle,
            "slider" => _slider,
            "choice" => _choice,
            _ => null
        };

        public void SetSettingValue(string key, object? value)
        {
            switch (key)
            {
                case "toggle":
                    _toggle = value is bool toggle && toggle;
                    break;
                case "slider":
                    _slider = value is double d ? d : _slider;
                    break;
                case "choice":
                    _choice = value as string ?? _choice;
                    break;
            }
        }
    }
}
