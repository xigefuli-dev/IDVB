using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{
    // 失败计数达到该值即视为"不可信任"，命中缓存时跳过该条目。
    public const int MaximumFailedValidationCountBeforeDistrust = 2;
}
