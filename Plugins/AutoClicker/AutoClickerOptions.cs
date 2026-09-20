using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoClicker;

/// <summary>
/// 连点器的可调计时段。默认按下 5ms + 抬手 10ms = 15ms，等价于旧的
/// <see cref="AutoClickerPolicy.ClickIntervalMilliseconds"/> 周期。
/// setter 做纵深防御钳制；字段用 volatile 保证 UI 线程写入与钩子/连点
/// 线程读取之间可见且不撕裂（x64 上 32 位读写本就有原子性）。
/// </summary>
public sealed class AutoClickerOptions
{
    public const int DefaultKeyDownDelayMilliseconds = 5;
    public const int DefaultUpToNextDownDelayMilliseconds = 10;
    public const int MinDelayMilliseconds = 1;
    public const int MaxKeyDownDelayMilliseconds = 50;
    public const int MaxUpToNextDownDelayMilliseconds = 100;
    public const int MinimumRandomDelayMilliseconds = 30;
    public const int MinimumRandomDelayUpperBoundMilliseconds = 50;
    public const int DefaultMaximumRandomDelayMilliseconds = 50;
    public const int MaximumRandomDelayMilliseconds = 10000;

    private volatile int _keyDownDelayMilliseconds = DefaultKeyDownDelayMilliseconds;
    private volatile int _upToNextDownDelayMilliseconds = DefaultUpToNextDownDelayMilliseconds;
    private volatile int _minimumRandomDelayMilliseconds = MinimumRandomDelayMilliseconds;
    private volatile int _maximumRandomDelayMilliseconds = DefaultMaximumRandomDelayMilliseconds;

    /// <summary>按下后延迟：F↓ 后保持的时长。</summary>
    public int KeyDownDelayMilliseconds
    {
        get => _keyDownDelayMilliseconds;
        set => _keyDownDelayMilliseconds = Math.Clamp(
            value, MinDelayMilliseconds, MaxKeyDownDelayMilliseconds);
    }

    /// <summary>抬手后延迟：F↑ 后到下一次 F↓ 的间隔。</summary>
    public int UpToNextDownDelayMilliseconds
    {
        get => _upToNextDownDelayMilliseconds;
        set => _upToNextDownDelayMilliseconds = Math.Clamp(
            value, MinDelayMilliseconds, MaxUpToNextDownDelayMilliseconds);
    }

    public int RandomDelayLowerBoundMilliseconds
    {
        get => _minimumRandomDelayMilliseconds;
        set => _minimumRandomDelayMilliseconds = Math.Clamp(
            value, PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayMilliseconds), MaximumRandomDelayMilliseconds);
    }

    public int RandomDelayUpperBoundMilliseconds
    {
        get => _maximumRandomDelayMilliseconds;
        set => _maximumRandomDelayMilliseconds = Math.Clamp(
            value, PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayUpperBoundMilliseconds), MaximumRandomDelayMilliseconds);
    }

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var first = RandomDelayLowerBoundMilliseconds;
        var second = RandomDelayUpperBoundMilliseconds;
        return (Math.Min(first, second), Math.Max(first, second));
    }

    public int NextRandomDelayMilliseconds()
    {
        var (minimum, maximum) = GetOrderedRandomDelayRange();
        return Random.Shared.Next(minimum, maximum + 1);
    }

    /// <summary>一次完整 F↓/F↑ 的周期。</summary>
    public int TotalPeriodMilliseconds =>
        KeyDownDelayMilliseconds + UpToNextDownDelayMilliseconds;

    public long KeyDownTicks(double tickRate) =>
        (long)(KeyDownDelayMilliseconds * tickRate / 1000.0);

    public long UpToNextDownTicks(double tickRate) =>
        (long)(UpToNextDownDelayMilliseconds * tickRate / 1000.0);

    public long PeriodTicks(double tickRate) =>
        (long)(TotalPeriodMilliseconds * tickRate / 1000.0);
}
