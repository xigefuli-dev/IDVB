using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public readonly record struct DisplayTestProfile(
    string Name,
    int PixelWidth,
    int PixelHeight,
    int ScalePercent,
    uint Dpi)
{
    public double ScaleFactor => ScalePercent / 100d;
    public int LogicalWidth => (int)Math.Round(PixelWidth / ScaleFactor);
    public int LogicalHeight => (int)Math.Round(PixelHeight / ScaleFactor);
    public MapScreenRect PhysicalBounds =>
        new(0d, 0d, PixelWidth, PixelHeight);

    public MapWindowSignature CreateSignature(
        int windowHandle = 42,
        int clientX = 100,
        int clientY = 80)
    {
        var viewportX = (int)Math.Round(PixelWidth * 0.12d);
        var viewportY = (int)Math.Round(PixelHeight * 0.10d);
        var viewportWidth = (int)Math.Round(PixelWidth * 0.74d);
        var viewportHeight = (int)Math.Round(PixelHeight * 0.72d);
        return new MapWindowSignature
        {
            WindowHandle = windowHandle,
            ClientX = clientX,
            ClientY = clientY,
            ClientWidth = PixelWidth,
            ClientHeight = PixelHeight,
            ViewportX = clientX + viewportX,
            ViewportY = clientY + viewportY,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
            Dpi = Dpi
        };
    }
}

/// <summary>
/// Shared physical-pixel display baseline for every resolution/DPI-sensitive test.
/// 1K follows the common gaming label for 1920x1080 (Full HD).
/// </summary>
public static class DisplayTestMatrix
{
    private static readonly (string Name, int Width, int Height)[] Resolutions =
    [
        ("1K-16:9", 1920, 1080),
        ("2K-16:9", 2560, 1440),
        ("2K-16:10", 2560, 1600),
        ("4K-16:9", 3840, 2160)
    ];

    private static readonly int[] ScalePercents = [100, 125, 150];

    public static DisplayTestProfile Baseline { get; } =
        Create("2K-16:9", 2560, 1440, 100);

    public static IReadOnlyList<DisplayTestProfile> Profiles { get; } =
        Resolutions
            .SelectMany(resolution => ScalePercents.Select(scale => Create(
                resolution.Name,
                resolution.Width,
                resolution.Height,
                scale)))
            .ToArray();

    public static IEnumerable<object[]> All => Profiles.Select(profile =>
        new object[]
        {
            profile.Name,
            profile.PixelWidth,
            profile.PixelHeight,
            profile.ScalePercent,
            profile.Dpi
        });

    public static DisplayTestProfile From(
        string name,
        int pixelWidth,
        int pixelHeight,
        int scalePercent,
        uint dpi)
    {
        var profile = Create(name, pixelWidth, pixelHeight, scalePercent);
        if (profile.Dpi != dpi)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        return profile;
    }

    private static DisplayTestProfile Create(
        string name,
        int width,
        int height,
        int scalePercent) =>
        new(
            name,
            width,
            height,
            scalePercent,
            checked((uint)(96 * scalePercent / 100)));
}
