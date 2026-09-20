using System.Globalization;

namespace IdentityVisionBridge.PluginRuntime;

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var withoutBuild = value.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3 ||
            !int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) ||
            major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, parts.Length == 2 ? parts[1] : null);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }
}

internal static class SemanticVersionRange
{
    public static bool Contains(string range, string value)
    {
        if (!SemanticVersion.TryParse(value, out var version) || string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var terms = range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => Evaluate(term, version));
    }

    private static bool Evaluate(string term, SemanticVersion value)
    {
        if (term == "*")
        {
            return true;
        }

        var operation = term.StartsWith(">=", StringComparison.Ordinal) || term.StartsWith("<=", StringComparison.Ordinal)
            ? term[..2]
            : term.StartsWith('>') || term.StartsWith('<') || term.StartsWith('=') || term.StartsWith('^')
                ? term[..1]
                : "=";
        var versionText = operation == "=" && !term.StartsWith('=') ? term : term[operation.Length..];
        if (!SemanticVersion.TryParse(versionText, out var target))
        {
            return false;
        }

        var comparison = value.CompareTo(target);
        return operation switch
        {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            "^" => comparison >= 0 && value.Major == target.Major,
            _ => comparison == 0
        };
    }
}
