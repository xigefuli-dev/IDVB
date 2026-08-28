using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    /// <summary>
    /// 缓存条目是否仍被无条件信任。失败计数达到门槛即降级，
    /// 即使 DirectlyTrusted（Manual/Player）也会降级——这正是错误玩家缩放被淘汰的前提。
    /// null Validation 视为可信（向后兼容无验证元数据的历史条目）。
    /// </summary>
    public static bool IsCacheEntryTrusted(MapFeatureCacheEntry? entry) =>
        entry is not null
        && entry.Scale is { } scale
        && (entry.Key.Channel != "low_structure"
            || scale.Validation?.DirectlyTrusted == true
            || (scale.Source == MapFeatureCacheSource.Recovery
                && scale.Validation?.LowStructureTrustLevel
                    == LowStructureCacheTrustLevel.Trusted))
        && scale.Validation is not
        {
            FailedValidationCount: >= MaximumFailedValidationCountBeforeDistrust
        };
}
