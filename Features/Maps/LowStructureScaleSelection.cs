namespace IDVBuff.Features.Maps;

internal sealed record LowStructureScaleSelection(
    IReadOnlyList<double> Scales,
    double RelativeResolution,
    int BasinCount,
    bool Ambiguous,
    double ElapsedMilliseconds = 0d,
    double BestCost = double.PositiveInfinity,
    double SecondCost = double.PositiveInfinity,
    double? HintScale = null,
    double HintConfidence = 0d,
    double SearchMinimumScale = 0d,
    double SearchMaximumScale = 0d,
    IReadOnlyList<double>? TopBasinScales = null);

internal static class LowStructureScaleSelectionContext
{
    private static readonly AsyncLocal<LowStructureScaleSelection?> CurrentSelection = new();

    public static LowStructureScaleSelection? Current
    {
        get => CurrentSelection.Value;
        set => CurrentSelection.Value = value;
    }
}
