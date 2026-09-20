using IDVBuff.PluginContracts;
using IDVBuff.Plugins.CustomPhrases;
using System.Runtime.Versioning;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[SupportedOSPlatform("windows")]
public sealed class CustomPhrasePluginTests
{
    [Fact]
    public void Plugin_ExposesChatAndPhraseBindingsAndThirtyPhraseTextSetting()
    {
        var plugin = new CustomPhrasePlugin();

        Assert.Equal(
            [
                "chat-menu-binding",
                "enable-mouse-binding",
                "phrase-menu-binding",
                "minimum-random-delay-ms",
                "maximum-random-delay-ms",
                "phrases"
            ],
            plugin.Settings.Select(setting => setting.Key).ToArray());
        Assert.Equal("keyboard:D:0", plugin.GetSettingValue("chat-menu-binding"));
        Assert.Equal("keyboard:C0:0", plugin.GetSettingValue("enable-mouse-binding"));
        Assert.Equal("keyboard:5D:0", plugin.GetSettingValue("phrase-menu-binding"));
        Assert.Equal(30d, plugin.GetSettingValue("minimum-random-delay-ms"));
        Assert.Equal(50d, plugin.GetSettingValue("maximum-random-delay-ms"));

        var phrases = Assert.IsType<PluginTextSetting>(
            plugin.Settings.Single(setting => setting.Key == "phrases"));
        Assert.False(phrases.Multiline);
        Assert.Equal(4096, phrases.MaxLength);
        Assert.Equal(CustomPhrasePluginData.MaxPhraseCount, phrases.MaxLineCount);

        plugin.SetSettingValue(
            "phrases",
            "第一条\r\n\n第二条\n第三条\n第四条");

        Assert.Equal(
            $"第一条{Environment.NewLine}{Environment.NewLine}第二条{Environment.NewLine}第三条{Environment.NewLine}第四条",
            plugin.GetSettingValue("phrases"));
        Assert.Equal(
            ["第一条", "第二条", "第三条", "第四条"],
            CustomPhrasePluginData.ParsePhrases(
                Assert.IsType<string>(plugin.GetSettingValue("phrases"))));

        plugin.SetSettingValue("phrases", "第一条\r\n");
        Assert.Equal($"第一条{Environment.NewLine}", plugin.GetSettingValue("phrases"));

        plugin.SetSettingValue("phrases", "第一条\r\n第二条");
        Assert.Equal(
            $"第一条{Environment.NewLine}第二条",
            plugin.GetSettingValue("phrases"));
    }

    [Fact]
    public void RandomDelay_UsesTheSharedSafetyPolicyAndOrdersTheRange()
    {
        var options = new CustomPhraseOptions
        {
            MinimumRandomDelayMilliseconds = 80,
            MaximumRandomDelayMilliseconds = 50
        };

        Assert.Equal((50, 80), options.GetOrderedRandomDelayRange());
        Assert.True(options.MinimumRandomDelayMilliseconds >=
            PluginRandomDelayPolicy.GetMinimum(30));
        Assert.True(options.MaximumRandomDelayMilliseconds >=
            PluginRandomDelayPolicy.GetMinimum(50));
    }

    [Fact]
    public void PhraseData_StopsAtThirtyAndUsesFiveCharacterDisplayLimit()
    {
        Assert.Equal(6000, CustomPhrasePluginData.SendCooldownMilliseconds);
        var raw = string.Join('\n', Enumerable.Range(1, 31).Select(index => $"短语 {index}"));

        Assert.Equal(30, CustomPhrasePluginData.ParsePhrases(raw).Count);
        Assert.Equal("自定义短…", CustomPhrasePluginData.ToDisplayText("自定义短语内容"));
        Assert.Equal("五个字啊哦", CustomPhrasePluginData.ToDisplayText("五个字啊哦"));
        var textSetting = Assert.IsType<PluginTextSetting>(
            new CustomPhrasePlugin().Settings.Single(setting => setting.Key == "phrases"));
        var thirtyOneLines = string.Join(
            "\n",
            Enumerable.Range(1, 31).Select(index => $"短语 {index}"));
        Assert.Equal(30, textSetting.Coerce(thirtyOneLines).Split(Environment.NewLine).Length);
        Assert.Equal(new PluginNormalizedPoint(0.8566, 0.8125),
            CustomPhrasePluginData.ChatBoxCoordinate16By9);
        Assert.Equal(new PluginNormalizedPoint(0.8570, 0.7325),
            CustomPhrasePluginData.ChatBoxCoordinate16By10);
    }

    [Theory]
    [InlineData(1920, 1080, 0.8566, 0.8125)]
    [InlineData(2560, 1440, 0.8566, 0.8125)]
    [InlineData(1920, 1200, 0.8570, 0.7325)]
    [InlineData(2560, 1600, 0.8570, 0.7325)]
    public void ChatBoxCoordinate_SelectsExactAspectRatio(
        int width,
        int height,
        double expectedX,
        double expectedY)
    {
        Assert.True(CustomPhrasePluginData.TryGetChatBoxCoordinate(
            width, height, out var coordinate));
        Assert.Equal(new PluginNormalizedPoint(expectedX, expectedY), coordinate);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, 1201)]
    [InlineData(2560, 1080)]
    public void ChatBoxCoordinate_RejectsUnsupportedClientBounds(int width, int height)
    {
        Assert.False(CustomPhrasePluginData.TryGetChatBoxCoordinate(
            width, height, out _));
    }
}
