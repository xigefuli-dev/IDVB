using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static bool IsReliableLocalizationSample(
        MapRecognitionResult result,
        double minimumLocalizationConfidence,
        double minimumCandidateMargin)
    {
        ArgumentNullException.ThrowIfNull(result);
        var confidence = result.LocalizationConfidence;
        var margin = GetCandidateMargin(result);
        return double.IsFinite(confidence)
            && confidence >= Math.Clamp(minimumLocalizationConfidence, 0d, 1d)
            && double.IsFinite(margin)
            && margin >= Math.Max(0d, minimumCandidateMargin);
    }
}
