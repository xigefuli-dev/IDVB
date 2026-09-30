namespace IDVBuff.Features.Feedback;

/// <summary>
/// 反馈问题描述文本校验器。
/// 规则：描述必须填写且字符数大于 10；中文汉字按 2 字符计算，其余字符按 1 字符计算。
/// </summary>
public static class FeedbackTextValidator
{
    public const int MinimumLengthExclusive = 10;

    public static bool IsContactQqValid(string? text)
    {
        var value = text?.Trim();
        return value is { Length: >= 5 and <= 12 }
            && value[0] >= '1' && value[0] <= '9'
            && value.All(c => c >= '0' && c <= '9');
    }

    /// <summary>
    /// 判断单个字符是否为中文字符（包含汉字、扩展表意文字以及中文全角标点符号）。
    /// </summary>
    public static bool IsChineseCharacter(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF)      // CJK Unified Ideographs (常用汉字)
            || (c >= 0x3400 && c <= 0x4DBF)      // CJK Unified Ideographs Extension A
            || (c >= 0xF900 && c <= 0xFAFF)      // CJK Compatibility Ideographs
            || (c >= 0x3000 && c <= 0x303F)      // CJK 标点符号 (、 。 《 》 【 】 等)
            || (c >= 0xFF01 && c <= 0xFF5E)      // 全角 ASCII 标点与字符 (， ！ ？ ； ： 等)
            || c == '\u2014' || c == '\u2026'    // —— 与 ……
            || c == '\u2018' || c == '\u2019' || c == '\u201C' || c == '\u201D'; // ‘’ 与 “”
    }

    /// <summary>
    /// 判断 Unicode 代码点是否为中文汉字或扩展表意文字（支持扩展表意文字代理对）。
    /// </summary>
    public static bool IsChineseCodePoint(int codePoint)
    {
        return (codePoint >= 0x4E00 && codePoint <= 0x9FFF)
            || (codePoint >= 0x3400 && codePoint <= 0x4DBF)
            || (codePoint >= 0xF900 && codePoint <= 0xFAFF)
            || (codePoint >= 0x3000 && codePoint <= 0x303F)
            || (codePoint >= 0xFF01 && codePoint <= 0xFF5E)
            || codePoint == 0x2014 || codePoint == 0x2026
            || codePoint == 0x2018 || codePoint == 0x2019 || codePoint == 0x201C || codePoint == 0x201D
            || (codePoint >= 0x20000 && codePoint <= 0x2EBEF); // CJK Extension B-F
    }

    /// <summary>
    /// 计算文本的加权字符数（中文字符/全角字符算 2 字符，其他字符算 1 字符，首尾空白不计入）。
    /// </summary>
    public static int CalculateWeightedLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var trimmed = text.Trim();
        int length = 0;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsHighSurrogate(trimmed[i]) && i + 1 < trimmed.Length && char.IsLowSurrogate(trimmed[i + 1]))
            {
                int codePoint = char.ConvertToUtf32(trimmed, i);
                length += IsChineseCodePoint(codePoint) ? 2 : 1;
                i++; // 跳过 low surrogate
            }
            else
            {
                length += IsChineseCharacter(trimmed[i]) ? 2 : 1;
            }
        }

        return length;
    }

    /// <summary>
    /// 校验描述是否有效（必须填写且字符数大于 10）。
    /// </summary>
    public static bool IsDescriptionValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return CalculateWeightedLength(text) > MinimumLengthExclusive;
    }
}
