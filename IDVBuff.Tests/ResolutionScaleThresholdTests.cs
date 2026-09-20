using System.Globalization;

namespace IDVBuff.Tests;

public sealed class ResolutionScaleThresholdTests
{
    public static TheoryData<string, double, double, double, int> PresetThresholds =>
        new()
        {
            { "1600x900", 0.32d, 0.30d, 1.70d, 15 },
            { "1920x1080", 0.40d, 0.35d, 1.70d, 15 },
            { "2560x1080", 0.45d, 0.35d, 1.70d, 15 },
            { "2560x1440", 0.45d, 0.35d, 1.70d, 15 },
            { "2560x1600", 0.50d, 0.40d, 1.70d, 14 },
            { "3440x1440", 0.45d, 0.35d, 1.70d, 15 }
        };

    [Theory]
    [MemberData(nameof(PresetThresholds))]
    public void ResolutionPresetCoversItsExpectedScaleDomain(
        string preset,
        double expectedSideMinimum,
        double expectedLowStructureMinimum,
        double expectedLowStructureMaximum,
        int expectedLowStructureHypotheses)
    {
        var presetRoot = Path.Combine(
            FindRepositoryRoot(),
            "Infrastructure",
            "Configuration",
            "Presets",
            preset);

        Assert.Equal(
            expectedSideMinimum,
            ReadNumber(Path.Combine(presetRoot, "side_entrance.toml"), "minimum_scale"),
            8);
        Assert.Equal(
            expectedLowStructureMinimum,
            ReadNumber(Path.Combine(presetRoot, "low_structure.toml"), "minimum_scale"),
            8);
        Assert.Equal(
            expectedLowStructureMaximum,
            ReadNumber(Path.Combine(presetRoot, "low_structure.toml"), "maximum_scale"),
            8);
        Assert.Equal(
            (double)expectedLowStructureHypotheses,
            ReadNumber(Path.Combine(presetRoot, "low_structure.toml"), "scale_hypothesis_count"));
    }

    private static double ReadNumber(string path, string key)
    {
        var prefix = key + " =";
        var line = File.ReadLines(path)
            .Select(value => value.Trim())
            .First(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return double.Parse(
            line[(line.IndexOf('=') + 1)..].Trim(),
            CultureInfo.InvariantCulture);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
