using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.CustomPhrases;

public sealed class CustomPhraseOptions
{
    public const int MaximumDelayMilliseconds = 10000;
    public const int MinimumRandomDelayMillisecondsAllowed = 30;
    public const int MinimumRandomDelayUpperBoundMillisecondsAllowed = 50;

    private int _minimumRandomDelayMilliseconds = 30;
    private int _maximumRandomDelayMilliseconds = 50;

    public int MinimumRandomDelayMilliseconds
    {
        get => _minimumRandomDelayMilliseconds;
        set => _minimumRandomDelayMilliseconds = Math.Clamp(value,
            PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayMillisecondsAllowed),
            MaximumDelayMilliseconds);
    }

    public int MaximumRandomDelayMilliseconds
    {
        get => _maximumRandomDelayMilliseconds;
        set => _maximumRandomDelayMilliseconds = Math.Clamp(value,
            PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayUpperBoundMillisecondsAllowed),
            MaximumDelayMilliseconds);
    }

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var minimum = Math.Clamp(MinimumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayMillisecondsAllowed),
            MaximumDelayMilliseconds);
        var maximum = Math.Clamp(MaximumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayUpperBoundMillisecondsAllowed),
            MaximumDelayMilliseconds);
        return (Math.Min(minimum, maximum), Math.Max(minimum, maximum));
    }
}
