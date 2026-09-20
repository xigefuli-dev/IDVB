using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static bool CanReplaceExistingEntry(
        MapFeatureCacheEntry? existing,
        MapFeatureCacheEntry replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var existingSource = existing?.Scale.Source;
        // Manual and Player entries are directly trusted bindings: an
        // automatic or recovery entry may only displace them after three
        // consistent recovery samples accumulate.
        if (existingSource is not (MapFeatureCacheSource.Manual
            or MapFeatureCacheSource.Player))
        {
            return true;
        }

        var validation = replacement.Scale.Validation;
        return replacement.Scale.Source == MapFeatureCacheSource.Recovery
            && replacement.Scale.SampleCount >= MinimumRepairValidationSamples
            && replacement.Scale.RelativeMedianAbsoluteDeviation
                <= MapScaleSampleAggregator.MaximumRelativeTolerance
            && validation is
            {
                SuccessfulValidationCount:
                    >= MinimumRepairValidationSamples
            };
    }
}
