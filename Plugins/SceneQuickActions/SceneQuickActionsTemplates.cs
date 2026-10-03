using OpenCvSharp;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 场景识别模板的元数据与加载。模板 PNG 由用户提供的 1920×1080 游戏截图裁剪，
/// 以嵌入资源方式随插件程序集一起分发（无需外部文件）。
///
/// 注意：「拾取全部」只裁了文字、刻意不带左边的按键框——框里显示的是玩家自己的键位，
/// 一旦改键就会变，带框会导致识别失败。
/// </summary>
internal static class SceneQuickActionsTemplates
{
    public const string AcceptButton = "accept_button";
    public const string PickupAll = "pickup_all";
    public const string QuickReplace = "quick_replace";

    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>模板在 1920×1080 参考帧中的位置，仅用于日志与参考。</summary>
    private static readonly Dictionary<string, Rect> ReferenceBoxes = new(StringComparer.Ordinal)
    {
        [AcceptButton] = new(1724, 288, 134, 56),
        [PickupAll] = new(1432, 564, 146, 52),
        [QuickReplace] = new(1418, 342, 144, 52)
    };

    /// <summary>搜索区域（相对客户区归一化），把匹配范围限制在界面实际出现的区域内。</summary>
    private static readonly Dictionary<string, (double X1, double Y1, double X2, double Y2)> SearchRegions =
        new(StringComparer.Ordinal)
        {
            [AcceptButton] = (0.78, 0.16, 1.00, 0.42),
            [PickupAll] = (0.60, 0.24, 0.95, 0.66),
            [QuickReplace] = (0.60, 0.24, 0.95, 0.66)
        };

    public static IReadOnlyCollection<string> All { get; } =
        [AcceptButton, PickupAll, QuickReplace];

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
        var assembly = typeof(SceneQuickActionsTemplates).Assembly;
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
