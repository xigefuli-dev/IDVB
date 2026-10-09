using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static string ComputeContentFingerprint(MapRecord map)
    {
        if (map.Floors.Count > 0 && map.Floors.All(floor => floor.SharedStructure is not null))
        {
            var structuralContent = string.Join('|', MapFloorRules.GetOrderedFloors(map)
                .Select(floor => ComputeContentFingerprint(map, floor.Key)));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(structuralContent))).ToLowerInvariant();
        }
        var builder = new StringBuilder()
            .Append(map.Id.ToString("N")).Append('|')
            .Append(map.ContentVersion).Append('|')
            .Append(map.UpdatedAt.UtcTicks);
        foreach (var floor in MapFloorRules.GetOrderedFloors(map))
        {
            builder.Append('|').Append(floor.Key)
                .Append('|').Append(floor.ImageSha256)
                .Append('|').Append(floor.ImageWidth).Append('x').Append(floor.ImageHeight)
                .Append('|').Append(floor.RecognitionSha256)
                .Append('|').Append(floor.RecognitionWidth).Append('x').Append(floor.RecognitionHeight)
                .Append('|').Append(floor.OverlaySha256)
                .Append('|').Append(floor.OverlayWidth).Append('x').Append(floor.OverlayHeight);
        }
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    public static string ComputeContentFingerprint(MapRecord map, string floorKey)
    {
        var floor = map.Floors.Single(candidate => string.Equals(candidate.Key, floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor.SharedStructure is not { } structure)
            return ComputeContentFingerprint(map);
        var content = $"{map.Id:N}|{floor.Key}|{structure.Id:N}|{structure.Revision}|{structure.UpdatedAt.UtcTicks}"
            + $"|{floor.RecognitionSha256}|{floor.RecognitionWidth}x{floor.RecognitionHeight}"
            + $"|{floor.PrebuiltStructureLine?.Sha256}|{floor.PrebuiltStructureLine?.AlgorithmSha256}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
