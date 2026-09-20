using System.Text;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.CustomPhrases;

/// <summary>自定义短语插件的固定数据与文本规则。</summary>
public static class CustomPhrasePluginData
{
    public const int MaxPhraseCount = 30;
    public const int DisplayCharacterLimit = 5;
    public const int SendCooldownMilliseconds = 6000;
    public const uint SendVirtualKey = 0x0D;
    public static readonly PluginNormalizedPoint ChatBoxCoordinate16By9 =
        new(0.8566, 0.8125);
    public static readonly PluginNormalizedPoint ChatBoxCoordinate16By10 =
        new(0.8570, 0.7325);

    public static bool TryGetChatBoxCoordinate(
        int width,
        int height,
        out PluginNormalizedPoint coordinate)
    {
        coordinate = default;
        if (width <= 0 || height <= 0)
            return false;

        // 与自动加特林一致：只接受精确的游戏客户区比例，不用近似浮点判断。
        if ((long)width * 9 == (long)height * 16)
        {
            coordinate = ChatBoxCoordinate16By9;
            return true;
        }

        if ((long)width * 10 == (long)height * 16)
        {
            coordinate = ChatBoxCoordinate16By10;
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ParsePhrases(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static phrase => phrase.Trim())
            .Where(static phrase => phrase.Length > 0)
            .Take(MaxPhraseCount)
            .ToArray();
    }

    public static string CoerceEditorText(string? raw)
    {
        var value = raw ?? string.Empty;
        if (value.Length > 4096)
            value = value[..4096];

        return string.Join(
            Environment.NewLine,
            value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.None)
                .Take(MaxPhraseCount));
    }

    public static string NormalizePhrases(string? raw) =>
        string.Join(Environment.NewLine, ParsePhrases(raw));

    public static string ToDisplayText(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var runes = phrase.EnumerateRunes().ToArray();
        if (runes.Length <= DisplayCharacterLimit)
            return phrase;

        return string.Concat(
            runes.Take(DisplayCharacterLimit - 1))
            + "…";
    }
}
