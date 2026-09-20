using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static string ComputeContentFingerprint(MapRecord map)
    {
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
}
