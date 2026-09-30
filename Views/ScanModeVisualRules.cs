namespace IDVBuff.Views;

internal static class ScanModeVisualRules
{
    internal readonly record struct Cell(int Seed, float Progress, float Floor, float Range, bool Visible, float Size);

    internal static Cell LatticeCell(int column, int row, int columns)
    {
        // The wide DeepScan thumb covers the last quarter of this rail. Place
        // the bright core immediately BEFORE it, as in the fully-open reference.
        var p = Math.Clamp((column / (float)(columns - 1) - .24f) / .49f, 0, 1);
        var seed = unchecked(column * 73856093 ^ row * 19349663) & 0x7fffffff;
        var floor = .006f + .60f * p * p * p * (.86f + .14f * Noise(seed + 7));
        var ceiling = (.018f + .98f * MathF.Pow(p, 1.35f)) * (.60f + .40f * Noise(seed + 19));
        var densityPosition = Math.Clamp(p * 1.3f, 0, 1);
        var density = .015f + .985f * densityPosition * densityPosition * (3 - 2 * densityPosition);
        var visible = Noise(seed + 31) < density;
        return new(seed, p, floor, Math.Max(.004f, ceiling - floor), visible, 2.6f + .8f * Noise(seed + 43));
    }

    private static float Noise(int seed)
    {
        var value = unchecked((uint)seed * 747796405u + 2891336453u);
        value = unchecked(((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u);
        return ((value >> 22) ^ value) / (float)uint.MaxValue;
    }

    // Immediate velocity, with a short, gentle tail; no slow ease-in on input.
    internal static double ResponseProgress(double milliseconds, double duration)
    {
        var remaining = 1 - Math.Clamp(milliseconds / duration, 0, 1);
        return 1 - remaining * remaining * remaining * remaining * remaining;
    }

    internal static double PointerIndex(double centerX, double width, double expansion, int segments) =>
        Math.Clamp(centerX / width * (3 + expansion) - .5, 0, Math.Min(segments - 1, 2 + expansion));

    internal static double FillRightInset(double width, double leadingEdge) =>
        width - Math.Clamp(leadingEdge, 0, width);

    internal static int HitTest(double x, double width, double expansion, int segments) =>
        Math.Clamp((int)(x / width * (3 + expansion)), 0, segments - 1);
}
