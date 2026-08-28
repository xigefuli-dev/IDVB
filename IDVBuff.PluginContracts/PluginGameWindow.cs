namespace IDVBuff.PluginContracts;

/// <summary>插件需要的前台游戏客户区坐标。</summary>
public readonly record struct PluginClientBounds(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public (int X, int Y) ToScreenPoint(PluginNormalizedPoint point)
    {
        if (!IsValid)
            throw new InvalidOperationException("游戏客户区无效。");
        return (
            X + (int)Math.Round(Math.Clamp(point.X, 0d, 1d) * Width),
            Y + (int)Math.Round(Math.Clamp(point.Y, 0d, 1d) * Height));
    }
}

/// <summary>归一化客户区坐标，范围为 0–1。</summary>
public readonly record struct PluginNormalizedPoint(double X, double Y)
{
    public bool IsValid => double.IsFinite(X)
        && double.IsFinite(Y)
        && X >= 0d
        && X <= 1d
        && Y >= 0d
        && Y <= 1d;
}

/// <summary>
/// 插件访问宿主已验证的前台游戏客户区。插件不会因此读取任意后台窗口。
/// </summary>
public interface IPluginGameWindowService
{
    bool TryGetForegroundClientBounds(
        out PluginClientBounds clientBounds,
        out IntPtr windowHandle,
        out string failureReason);
}
