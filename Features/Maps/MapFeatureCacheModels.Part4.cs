using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    /// <summary>
    /// 语义修正 C：三次一致修复完成时生成全新验证元数据，失败计数清零，
    /// 避免携带毒缓存的失败历史导致新 Recovery 条目立即被降级。
    /// </summary>
    public static MapScaleCacheValidationMetadata CreateRepairValidation(
        MapCacheRepairAggregate aggregate) =>
        new()
        {
            DirectlyTrusted = false,
            SuccessfulValidationCount = aggregate.SampleCount,
            FailedValidationCount = 0,
            LastLocalizationConfidence = aggregate.LocalizationConfidence,
            LastCandidateMargin = aggregate.CandidateMargin,
            LastValidatedAt = DateTimeOffset.UtcNow
        };
}
