namespace IDVBuff.PluginContracts;

/// <summary>宿主控制的插件随机延迟安全策略。</summary>
public static class PluginRandomDelayPolicy
{
    private static int _allowUnsafeMinimums;

    public static bool AllowUnsafeMinimums
    {
        get => Volatile.Read(ref _allowUnsafeMinimums) != 0;
        set => Volatile.Write(ref _allowUnsafeMinimums, value ? 1 : 0);
    }

    public static int GetMinimum(int safeMinimum) =>
        AllowUnsafeMinimums ? 0 : safeMinimum;
}
