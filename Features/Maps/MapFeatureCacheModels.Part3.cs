using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    /// <summary>
    /// 记录一次缓存验证结果。succeeded=true 且存在失败历史时重置失败计数
    /// （正向证据恢复信任）；succeeded=false 时失败计数 +1。返回 null 表示无需落盘
    /// （成功且无失败历史，快乐路径零写）。
    /// </summary>
    public static MapScaleCacheValidationMetadata? RecordValidationOutcome(
        MapScaleCacheValidationMetadata? current,
        bool succeeded,
        DateTimeOffset validatedAt)
    {
        if (succeeded)
        {
            if (current is null || current.FailedValidationCount == 0)
                return null;
            return new MapScaleCacheValidationMetadata
            {
                DirectlyTrusted = current.DirectlyTrusted,
                LowStructureTrustLevel = current.LowStructureTrustLevel,
                SuccessfulValidationCount =
                    current.SuccessfulValidationCount + 1,
                FailedValidationCount = 0,
                LastLocalizationConfidence = current.LastLocalizationConfidence,
                LastCandidateMargin = current.LastCandidateMargin,
                LastValidatedAt = validatedAt
            };
        }

        return new MapScaleCacheValidationMetadata
        {
            DirectlyTrusted = current?.DirectlyTrusted ?? false,
            LowStructureTrustLevel = current?.LowStructureTrustLevel
                ?? LowStructureCacheTrustLevel.None,
            SuccessfulValidationCount = current?.SuccessfulValidationCount ?? 0,
            FailedValidationCount = (current?.FailedValidationCount ?? 0) + 1,
            LastLocalizationConfidence =
                current?.LastLocalizationConfidence ?? 0d,
            LastCandidateMargin = current?.LastCandidateMargin ?? 0d,
            LastValidatedAt = validatedAt
        };
    }
}
