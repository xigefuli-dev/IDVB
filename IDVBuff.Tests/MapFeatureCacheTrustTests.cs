using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

/// <summary>
/// 缓存信任降级与 scale 兜底策略的纯数据模型测试。覆盖毒缓存（Player
/// scale 错误）的淘汰闭环：失败计数 → 降级跳过 → Recovery 修复 → 保护规则不豁免。
/// </summary>
public sealed class MapFeatureCacheTrustTests
{
    private static MapFeatureCacheKey Key() => new(
        Guid.NewGuid(),
        "content",
        "1f",
        new MapCacheResolutionSignature(1920, 1080, 1600, 900));

    [Fact]
    public void CacheEntryWithoutFailuresIsTrusted()
    {
        var key = Key();
        var noValidation = new MapFeatureCacheEntry
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1.0d,
                Source = MapFeatureCacheSource.Manual,
                SampleCount = 1,
                Confidence = 0.9d,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        var zeroFailures = new MapFeatureCacheEntry
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1.0d,
                Source = MapFeatureCacheSource.Player,
                SampleCount = 1,
                Confidence = 1.0d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = true,
                    LastValidatedAt = DateTimeOffset.UtcNow
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        Assert.True(MapFeatureCacheRules.IsCacheEntryTrusted(noValidation));
        Assert.True(MapFeatureCacheRules.IsCacheEntryTrusted(zeroFailures));
        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(null));
    }

    [Fact]
    public void CacheEntryBecomesDistrustedAtThresholdBoundary()
    {
        var key = Key();
        var threshold =
            MapFeatureCacheRules.MaximumFailedValidationCountBeforeDistrust;
        MapFeatureCacheEntry Entry(int failures) => new()
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1.0d,
                Source = MapFeatureCacheSource.Automatic,
                SampleCount = 1,
                Confidence = 0.9d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    FailedValidationCount = failures,
                    LastValidatedAt = DateTimeOffset.UtcNow
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        Assert.True(MapFeatureCacheRules.IsCacheEntryTrusted(Entry(threshold - 1)));
        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(Entry(threshold)));
        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(Entry(threshold + 1)));
    }

    [Fact]
    public void DirectlyTrustedPlayerEntryStillDistrustsAfterRepeatedFailures()
    {
        var key = Key();
        var player = new MapFeatureCacheEntry
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 0.899d,
                Source = MapFeatureCacheSource.Player,
                SampleCount = 1,
                Confidence = 1.0d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = true,
                    FailedValidationCount =
                        MapFeatureCacheRules.MaximumFailedValidationCountBeforeDistrust,
                    LastValidatedAt = DateTimeOffset.UtcNow
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        // DirectlyTrusted 不豁免：连续验证失败后玩家缩放同样被降级，
        // 这正是错误玩家缩放被淘汰的前提。
        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(player));
    }

    [Fact]
    public void FailedValidationOutcomeIncrementsFailureCount()
    {
        var now = DateTimeOffset.UtcNow;
        var first = MapFeatureCacheRules.RecordValidationOutcome(
            current: null,
            succeeded: false,
            validatedAt: now);
        Assert.NotNull(first);
        Assert.Equal(1, first!.FailedValidationCount);

        var second = MapFeatureCacheRules.RecordValidationOutcome(
            first,
            succeeded: false,
            validatedAt: now.AddSeconds(1));
        Assert.NotNull(second);
        Assert.Equal(2, second!.FailedValidationCount);
        Assert.Equal(0, second.SuccessfulValidationCount);
    }

    [Fact]
    public void SuccessfulValidationOutcomeResetsFailureCount()
    {
        var now = DateTimeOffset.UtcNow;
        var failed = MapFeatureCacheRules.RecordValidationOutcome(
            current: null,
            succeeded: false,
            validatedAt: now)!;
        Assert.Equal(1, failed.FailedValidationCount);

        var reset = MapFeatureCacheRules.RecordValidationOutcome(
            failed,
            succeeded: true,
            validatedAt: now.AddSeconds(1));
        Assert.NotNull(reset);
        Assert.Equal(0, reset!.FailedValidationCount);
        Assert.Equal(1, reset.SuccessfulValidationCount);

        // 无失败历史时成功 → null（快乐路径零写）。
        var noHistory = new MapScaleCacheValidationMetadata
        {
            LastValidatedAt = now
        };
        Assert.Null(MapFeatureCacheRules.RecordValidationOutcome(
            noHistory,
            succeeded: true,
            validatedAt: now.AddSeconds(2)));
    }

    [Fact]
    public void RepairValidationStartsFreshAfterThreeConsistentSamples()
    {
        var aggregate = new MapCacheRepairAggregate(
            Scale: 0.99d,
            SampleCount: 3,
            LocalizationConfidence: 0.92d,
            CandidateMargin: 0.06d,
            RelativeMedianAbsoluteDeviation: 0.001d);
        var validation = MapFeatureCacheRules.CreateRepairValidation(aggregate);

        // 失败计数清零，不继承毒缓存历史 → 新 Recovery 条目立即可信。
        Assert.Equal(0, validation.FailedValidationCount);
        Assert.Equal(3, validation.SuccessfulValidationCount);
        Assert.False(validation.DirectlyTrusted);
        var entry = new MapFeatureCacheEntry
        {
            Key = Key(),
            Scale = new MapScaleCachePayload
            {
                UniformScale = aggregate.Scale,
                Source = MapFeatureCacheSource.Recovery,
                SampleCount = aggregate.SampleCount,
                Confidence = aggregate.LocalizationConfidence,
                RelativeMedianAbsoluteDeviation =
                    aggregate.RelativeMedianAbsoluteDeviation,
                Validation = validation,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        Assert.True(MapFeatureCacheRules.IsCacheEntryTrusted(entry));
    }

    [Fact]
    public void DistrustDoesNotBypassManualProtectionRule()
    {
        var key = Key();
        MapFeatureCacheEntry Entry(
            MapFeatureCacheSource source,
            int samples,
            int validations) => new()
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 0.99d,
                Source = source,
                SampleCount = samples,
                Confidence = 0.9d,
                RelativeMedianAbsoluteDeviation = 0.001d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    SuccessfulValidationCount = validations,
                    LastValidatedAt = validations > 0
                        ? DateTimeOffset.UtcNow
                        : default
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        var poisonedPlayer = new MapFeatureCacheEntry
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 0.899d,
                Source = MapFeatureCacheSource.Player,
                SampleCount = 1,
                Confidence = 1.0d,
                RelativeMedianAbsoluteDeviation = 0d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = true,
                    FailedValidationCount = 2,
                    LastValidatedAt = DateTimeOffset.UtcNow
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(poisonedPlayer));
        // 信任降级只影响"运行时是否跳过"，不削弱淘汰保护规则：
        // Automatic / PreprocessedEstimate 依旧不能替换降级的 Player 条目。
        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            poisonedPlayer,
            Entry(MapFeatureCacheSource.Automatic, 4, 4)));
        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            poisonedPlayer,
            Entry(MapFeatureCacheSource.PreprocessedEstimate, 1, 1)));
        Assert.True(MapFeatureCacheRules.CanReplaceExistingEntry(
            poisonedPlayer,
            Entry(MapFeatureCacheSource.Recovery, 3, 3)));
    }

    [Fact]
    public async Task RepositoryRoundTripsDistrustMetadata()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"idvb-map-cache-distrust-{Guid.NewGuid():N}");
        try
        {
            var repository = new MapFeatureCacheRepository(directory);
            await repository.InitializeAsync();
            var key = Key();
            await repository.UpsertAsync(new MapFeatureCacheEntry
            {
                Key = key,
                Scale = new MapScaleCachePayload
                {
                    UniformScale = 0.899d,
                    Source = MapFeatureCacheSource.Player,
                    SampleCount = 1,
                    Confidence = 1.0d,
                    RelativeMedianAbsoluteDeviation = 0d,
                    Validation = new MapScaleCacheValidationMetadata
                    {
                        DirectlyTrusted = true,
                        FailedValidationCount = 2,
                        LastValidatedAt = DateTimeOffset.UtcNow
                    },
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            });

            Assert.True(repository.TryGet(key, out var loaded));
            Assert.NotNull(loaded);
            // 降级标志可持久化：重新加载后仍不受信。
            Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(loaded));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CachedScaleRepairSearchPolicyUsesNarrowRadiusAndDisablesEarlyTermination()
    {
        var tuning = new MapStructureRegistrationTuning();

        MapOpenAlignmentRouteRules.ApplyCachedScaleRepairSearchPolicy(tuning);

        Assert.Equal(
            MapOpenAlignmentRouteRules.CachedScaleRepairSearchRadius,
            tuning.ScaleSearchRadius,
            6);
        Assert.True(tuning.DisableScaleEarlyTermination);
        Assert.False(tuning.EnableFastAlignment);
        Assert.False(tuning.EnableFeatureVoting);
        Assert.Equal(0d, tuning.TrackingScaleSearchRadius);
        // Normalize 后半径不被 clamp 吞掉。
        Assert.Equal(
            MapOpenAlignmentRouteRules.CachedScaleRepairSearchRadius,
            tuning.ScaleSearchRadius,
            6);
    }
}
