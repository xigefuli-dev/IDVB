using OpenCvSharp;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 「可拾取」面板与「误开的设置界面」的识别模板。三张 PNG 都是 1920×1080 游戏截图裁剪：
///   pickup_all      —— 面板下方的「拾取全部」文案（刻意不带左边的按键框，改键后照样认得出）；
///   quick_replace   —— 面板第一行右侧的「快捷替换」按钮；
///   settings_screen —— 设置界面底部按钮上的「退出战斗」文字。
/// 「可拾取」面板认出前两张里的任意一张即算打开（是否「一闪而过」由 Runner 按时间门槛判断）；
/// 设置界面单独认第三张，认出就补按一下 ESC 退回上一界面。
/// </summary>
internal static class PickupEscapeTemplates
{
    public const string PickupAll = "pickup_all";
    public const string QuickReplace = "quick_replace";

    /// <summary>
    /// 误开出来的游戏设置界面：那一下 ESC 没东西可关时（面板已经没了 / 一闪而过）游戏会开到这一层。
    /// 认出它就再补按一下 ESC 退回上一界面。
    /// </summary>
    public const string SettingsScreen = "settings_screen";

    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>模板在 1920×1080 参考帧中的位置，仅用于日志与参考。</summary>
    private static readonly Dictionary<string, Rect> ReferenceBoxes = new(StringComparer.Ordinal)
    {
        [PickupAll] = new(1432, 564, 146, 52),
        [QuickReplace] = new(1418, 342, 144, 52),
        [SettingsScreen] = new(1322, 858, 150, 52)
    };

    /// <summary>搜索区域（相对客户区归一化），把匹配范围限制在面板实际出现的区域内。</summary>
    private static readonly Dictionary<string, (double X1, double Y1, double X2, double Y2)> SearchRegions =
        new(StringComparer.Ordinal)
        {
            [PickupAll] = (0.60, 0.24, 0.95, 0.66),
            [QuickReplace] = (0.60, 0.24, 0.95, 0.66),
            // 设置界面的「退出战斗」在底部那一排按钮上。
            [SettingsScreen] = (0.60, 0.72, 0.98, 0.90)
        };

    public static IReadOnlyCollection<string> All { get; } =
        [PickupAll, QuickReplace, SettingsScreen];

    public static Rect GetReferenceBox(string name) => ReferenceBoxes[name];

    public static Rect GetSearchRegion(string name, Size frameSize)
    {
        var region = SearchRegions[name];
        var x1 = (int)Math.Round(region.X1 * frameSize.Width);
        var y1 = (int)Math.Round(region.Y1 * frameSize.Height);
        var x2 = (int)Math.Round(region.X2 * frameSize.Width);
        var y2 = (int)Math.Round(region.Y2 * frameSize.Height);
        x1 = Math.Clamp(x1, 0, Math.Max(0, frameSize.Width - 1));
        y1 = Math.Clamp(y1, 0, Math.Max(0, frameSize.Height - 1));
        x2 = Math.Clamp(x2, x1 + 1, frameSize.Width);
        y2 = Math.Clamp(y2, y1 + 1, frameSize.Height);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public static Mat LoadGray(string name)
    {
        var suffix = $".{name}.png";
        var assembly = typeof(PickupEscapeTemplates).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            candidate => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException($"找不到嵌入的识别模板：{name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取嵌入的识别模板：{resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var image = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Grayscale);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidOperationException($"识别模板解码失败：{name}");
        }

        return image;
    }
}
