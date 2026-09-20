namespace IDVBuff.Features.Maps;

/// <summary>Canonicalizes extensible per-floor algorithm markers.</summary>
public static class MapFloorMarkerRules
{
    public const string LowStructure = "low_structure";

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? markerKeys)
    {
        if (markerKeys is null)
            return [];

        return markerKeys
            .Where(IsValid)
            .Select(key => key.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsValid(string? markerKey)
    {
        if (string.IsNullOrWhiteSpace(markerKey))
            return false;

        var value = markerKey.Trim();
        if (value.Length > 64 || value[0] == '_' || value[^1] == '_')
            return false;

        var previousWasUnderscore = false;
        foreach (var character in value)
        {
            if (character == '_')
            {
                if (previousWasUnderscore)
                    return false;
                previousWasUnderscore = true;
                continue;
            }

            if ((character is < 'a' or > 'z')
                && (character is < 'A' or > 'Z')
                && (character is < '0' or > '9'))
            {
                return false;
            }

            previousWasUnderscore = false;
        }

        return true;
    }

    public static bool Has(IEnumerable<string>? markerKeys, string markerKey) =>
        Normalize(markerKeys).Contains(
            markerKey.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);
}
