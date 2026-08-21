namespace IDVBuff.Features.Maps;

internal static class MapOpenAlignmentRouteRules
{
    internal const int MinimumNoDoorStageBudgetMilliseconds = 250;
    internal const int InitialAlignmentMaximumMilliseconds = 1000;
    internal const int SteadyAlignmentMaximumMilliseconds = 200;
    // Kept as compatibility aliases for existing diagnostic readers. They are
    // never used to cancel or truncate alignment work.
    internal const int MinimumFeatureRecoveryBudgetMilliseconds = InitialAlignmentMaximumMilliseconds;
    internal const int TargetP50Milliseconds = SteadyAlignmentMaximumMilliseconds;
    internal const int TargetP95Milliseconds = InitialAlignmentMaximumMilliseconds;
    internal const int MaximumNoDoorAlignmentBudgetMilliseconds = InitialAlignmentMaximumMilliseconds;
    internal const int VpsgStageBudgetMilliseconds = 600;
    internal const int MinimumVpsgStageBudgetMilliseconds = 450;
    internal const double TargetReliableAlignmentRate = 0.95d;
    internal const double TargetTranslationJitterP95Pixels = 3d;
    // cached-scale 固定验证失败后的极小半径 Search 兜底：救缓存 scale 的小漂移，
    // 并为信任降级提供"成功→重置 / 失败→计数+1"的验证证据。
    internal const int CachedScaleRepairSearchBudgetMilliseconds = 300;
    internal const double CachedScaleRepairSearchRadius = 0.03d; // 覆盖 ±3%
    // 全局恢复（unrestricted 第二轮）的局部证据门槛：实测局部结构配准置信度
    // < 0.52（chamferQuality≈0 的"最佳候选绝对贴合度不足"）时全局第二轮 2/2
    // 白付 ~270ms；≥0.68 时 2/2 成功。0.52 只砍掉证据极弱的帧，保留接近成功
    // 的恢复机会，避免把注定失败的全局全尺度搜索跑成最慢路径。
    internal const double GlobalRecoveryMinimumLocalConfidence = 0.52d;

    internal static void ApplyCachedScaleRepairSearchPolicy(
        MapStructureRegistrationTuning tuning)
    {
        // 兜底必须走诚实的 EdgesOnly Search：固定 SchemaVersion 防止 Normalize
        // 按旧版本把 EnableFeatureVoting 强制翻回 true。
        tuning.SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion;
        tuning.ScaleSearchRadius = CachedScaleRepairSearchRadius;
        tuning.TrackingScaleSearchRadius = 0d;
        // 种子 scale 可能不准，禁止在错误 scale 上早停。
        tuning.DisableScaleEarlyTermination = true;
        // 走诚实的结构搜索，避免粗路径早退。
        tuning.EnableFastAlignment = false;
        // 强制 EdgesOnly → 复用 fixed 阶段已提取的实时特征，不重复 AKAZE。
        tuning.EnableFeatureVoting = false;
        tuning.Normalize();
    }

    internal static void ApplySteadyGlobalTranslationRecoveryPolicy(
        MapStructureRegistrationTuning tuning)
    {
        // Steady recovery expands translation only. The exact floor's reliable
        // scale remains immutable, and the search must be allowed to complete
        // instead of converting a performance target into a false failure.
        tuning.SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion;
        tuning.ScaleSearchRadius = 0d;
        tuning.TrackingScaleSearchRadius = 0d;
        tuning.EnableFeatureVoting = false;
        tuning.EnforceTimeBudget = false;
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = true;
        tuning.FastCoarseTopK = Math.Max(5, tuning.FastCoarseTopK);
        tuning.VisibleAwareTopK = Math.Max(5, tuning.VisibleAwareTopK);
        tuning.Normalize();
    }

    internal static bool ShouldPreferLockedSideFeature(
        bool isOtherFloor,
        bool recoveringSelectedIdentity,
        double sideEntrancePriorConfidence) =>
        !isOtherFloor
        && !recoveringSelectedIdentity
        && sideEntrancePriorConfidence > 0d;

    internal static SelectedAlignmentRoute ResolveMatchRoute(
        FirstScanStrategy firstScanStrategy,
        MapAlignmentSession? session)
    {
        if (session is
            {
                SideEntranceScanPriorConfidence: > 0d,
                HasGatePairLock: false
            })
        {
            return SelectedAlignmentRoute.SideEntrance;
        }

        if (session?.HasGatePairLock is true)
            return SelectedAlignmentRoute.Default;

        return firstScanStrategy == FirstScanStrategy.SideEntrance
            ? SelectedAlignmentRoute.SideEntrance
            : SelectedAlignmentRoute.Default;
    }

    internal static SelectedAlignmentRoute ResolvePendingIdentityRoute(
        MapAlignmentSession session) =>
        session.SideEntranceScanPriorConfidence > 0d
            && !session.HasGatePairLock
                ? SelectedAlignmentRoute.SideEntrance
                : SelectedAlignmentRoute.Default;

    internal static MapAlignmentSession ResolveMapOpenAlignmentSession(
        MapRecord map,
        MapRecognitionResult result,
        MapAlignmentSession? pendingSideEntranceSeed,
        MapAlignmentSession? previous,
        bool canReusePrevious,
        string? independentFloorKey = null)
    {
        if (pendingSideEntranceSeed is not null)
            return pendingSideEntranceSeed;

        if (previous is not null
            && previous.MapId == map.Id
            && previous.MapUpdatedAt == map.UpdatedAt)
        {
            // Adaptive scale reliability controls whether the old transform
            // may be reused, not which first-scan strategy owns this match.
            return canReusePrevious
                ? previous
                : MapAlignmentSession.RebuildPreservingFirstScanIdentity(
                    previous,
                    map,
                    result);
        }

        // A variant identity is committed before its first transform exists.
        // Starting that map from the previous variant's transform would leak
        // scale/translation across maps, while FromRecognition deliberately
        // rejects a transform-less result. Give the exact target floor its own
        // neutral seed so VPSG/cache/structure recovery can perform the first
        // honest alignment without touching another map or floor's evidence.
        if (!string.IsNullOrWhiteSpace(independentFloorKey)
            && result.OverlayTransform is null)
        {
            var transform = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                map,
                independentFloorKey);
            return new MapAlignmentSession
            {
                MapId = map.Id,
                MapUpdatedAt = map.UpdatedAt,
                FloorKey = independentFloorKey,
                LockedTransform = transform,
                BaselineGateScale = transform.ScaleX,
                HasGatePairLock = false,
                Mode = MapAlignmentTrackingMode.StructureMatched,
                SideEntranceScanPriorConfidence = 0d
            };
        }

        return MapAlignmentSession.FromRecognition(map, result);
    }

    internal static bool ShouldPrioritizeStructureValidation(
        SelectedAlignmentRoute route,
        bool hasAlignmentDeadline) =>
        route == SelectedAlignmentRoute.SideEntrance
        && hasAlignmentDeadline;

    internal static int ResolveNoDoorAlignmentBudgetMilliseconds(
        int configuredMilliseconds) =>
        Math.Clamp(
            configuredMilliseconds,
            MinimumNoDoorStageBudgetMilliseconds,
            MaximumNoDoorAlignmentBudgetMilliseconds);

    internal static double ResolveSingleGlobalRecoveryRadius(
        bool hasFloorCalibration) =>
        MapFloorScaleSearchPolicy.GetRadii(hasFloorCalibration).ExpandedRadius;

    internal static bool IsCompatibleReliableFloorSession(
        MapAlignmentSession? session,
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        string floorKey,
        double minimumConfidence)
    {
        if (session is null
            || session.MapId != mapId
            || session.MapUpdatedAt != mapUpdatedAt
            || !string.Equals(
                session.FloorKey,
                floorKey,
                StringComparison.Ordinal)
            || !double.IsFinite(session.LastConfidence)
            || session.LastConfidence < Math.Clamp(minimumConfidence, 0d, 1d))
        {
            return false;
        }

        return MapSimilarityTransform.FromOverlay(session.LockedTransform)
            .IsValid;
    }
}

internal static class MapNoDoorAlignmentBudgetContext
{
    private static readonly AsyncLocal<Func<int>?> RemainingAccessor = new();

    internal static int? RemainingMilliseconds => RemainingAccessor.Value?.Invoke();

    internal static IDisposable Enter(Func<int> remainingAccessor)
    {
        ArgumentNullException.ThrowIfNull(remainingAccessor);
        var previous = RemainingAccessor.Value;
        RemainingAccessor.Value = remainingAccessor;
        return new Lease(previous);
    }

    private sealed class Lease(Func<int>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            RemainingAccessor.Value = previous;
        }
    }
}
/*
 * 文件职责：MapOpenAlignmentRouteRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
