using System.Globalization;
using System.Text.RegularExpressions;

namespace IDVBuff.UpdateCore;

public static partial class UpdateVersionMapper
{
    public static string ToVelopackVersion(string publicVersion, string productVersion)
    {
        var publicMatch = PublicVersionPattern().Match(publicVersion);
        if (!publicMatch.Success)
            throw new FormatException("Expected public version bNN.N-YY.MM.DD.NNNN.");

        var productMatch = ProductVersionPattern().Match(productVersion);
        if (!productMatch.Success)
            throw new FormatException("Expected product version N.N.N[-prerelease].");

        var releaseMajor = int.Parse(publicMatch.Groups["major"].Value, CultureInfo.InvariantCulture);
        var releaseMinor = int.Parse(publicMatch.Groups["minor"].Value, CultureInfo.InvariantCulture);
        if (releaseMajor != int.Parse(productMatch.Groups["major"].Value, CultureInfo.InvariantCulture)
            || releaseMinor != int.Parse(productMatch.Groups["minor"].Value, CultureInfo.InvariantCulture))
        {
            throw new FormatException("The public release line does not match the product version.");
        }

        var date = "20" + publicMatch.Groups["year"].Value
            + publicMatch.Groups["month"].Value
            + publicMatch.Groups["day"].Value;
        var build = int.Parse(publicMatch.Groups["build"].Value, CultureInfo.InvariantCulture);
        return $"{productVersion}-build.{date}.{build}";
    }

    [GeneratedRegex("^b(?<major>\\d{2})\\.(?<minor>\\d+)-(?<year>\\d{2})\\.(?<month>\\d{2})\\.(?<day>\\d{2})\\.(?<build>\\d{4})$")]
    private static partial Regex PublicVersionPattern();

    [GeneratedRegex("^(?<major>\\d{1,3})\\.(?<minor>\\d{1,3})\\.(?<patch>\\d{1,3})(?:-(?<prerelease>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex ProductVersionPattern();
}
