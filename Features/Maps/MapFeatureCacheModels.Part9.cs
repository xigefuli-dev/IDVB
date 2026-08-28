using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static MapFeatureCacheKey CreateKey(
        MapRecord map,
        string floorKey,
        MapCacheResolutionSignature resolution,
        MapAlignmentChannel channel = MapAlignmentChannel.Standard,
        string configFingerprint = "legacy") =>
        new(
            map.Id,
            ComputeContentFingerprint(map),
            floorKey,
            resolution,
            channel == MapAlignmentChannel.LowStructure ? "low_structure" : "standard",
            configFingerprint);
}
