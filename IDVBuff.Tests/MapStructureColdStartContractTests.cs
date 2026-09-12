using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

/// <summary>
/// 契约回归：中性种子是占位符，不是证据。
/// <para>
/// 地图变体切换后主楼层会拿到一个 scale=1 / tx=ty=0 的中性种子。它只用来满足
/// 对象契约，绝不代表"真实 scale 是 1.0"。任何会把它升级成
/// Fixed / Tracking / Restricted 的路径都必须被封死：否则"等待重新对齐"会退化成
/// 围着错误尺度的一次注定失败的局部搜索，而且切回原变体也不会自行恢复。
/// </para>
/// <para>
/// 这些测试断言提取出来的纯规则（<see cref="MapOpenAlignmentRouteRules"/>），
/// 而不是 <c>AlignSelectedWithStructure</c> 内部的偶然分支。
/// </para>
/// </summary>
public sealed class MapStructureColdStartContractTests
{
    // ── 场景构造 ─────────────────────────────────────────────────────────────

    private static MapRecord CreateMap()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        map.Recognition.FirstFloor.RecognitionPixelWidth = 1000;
        map.Recognition.FirstFloor.RecognitionPixelHeight = 800;
        map.Recognition.SecondFloor.RecognitionPixelWidth = 900;
        map.Recognition.SecondFloor.RecognitionPixelHeight = 720;
        return map;
    }

    /// <summary>变体身份已提交、但首个 transform 尚未产生的识别结果。</summary>
    private static MapRecognitionResult TransformlessResult(
        MapRecord map,
        string floorKey) => new()
        {
            MapId = map.Id,
            Floor = floorKey,
            IdentityConfidence = 1d,
            LocalizationConfidence = 0d,
            OverlayTransform = null
        };

    /// <summary>一次真实测量得到的非中性变换（模拟单门/结构配准产出的证据）。</summary>
    private static MapOverlayTransform MeasuredTransform(double scale) => new()
    {
        ScaleX = scale,
        ScaleY = scale,
        OffsetX = -128d,
        OffsetY = 96d,
        ReferenceCenterX = 500d,
        ReferenceCenterY = 400d,
        ScreenCenterX = 240d,
        ScreenCenterY = 390d,
        ReferenceWidth = 1000,
        ReferenceHeight = 800,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };

    /// <summary>
    /// 复现真实到达路径：地图变体切换后，主楼层 1f 拿到本楼层的中性种子。
    /// </summary>
    private static MapAlignmentSession CreatePendingVariantPrimaryFloorSession(
        MapRecord map)
    {
        var session = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            map,
            TransformlessResult(map, "1f"),
            pendingSideEntranceSeed: null,
            previous: null,
            canReusePrevious: false,
            targetFloorKey: "1f");
        Assert.True(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(session.LockedTransform),
            "前置条件：待处理变体的主楼层会话必须是中性种子。");
        Assert.False(session.HasGatePairLock);
        return session;
    }

    private static MapStructureSearchRoute ResolveRoute(
        MapOverlayTransform structureSeed,
        bool hasFreshAnchorTransform = false,
        bool hasSingleGateProposal = false,
        bool isScanVerification = false,
        bool isSideEntranceStructureRoute = false,
        bool restrictStructureSearch = false,
        AlignmentSearchContext? searchContext = null) =>
        MapOpenAlignmentRouteRules.ResolveStructureSearchRoute(
            structureSeed,
            hasFreshAnchorTransform,
            hasSingleGateProposal,
            isScanVerification,
            isSideEntranceStructureRoute,
            restrictStructureSearch,
            searchContext);

    private static void AssertUnknownTransformColdStart(
        MapStructureSearchRoute route)
    {
        Assert.Equal(MapStructureSeedKind.Neutral, route.SeedKind);
        Assert.True(
            route.UnknownTransformColdStart,
            "中性种子 + 无门 + 无锚点必须被识别为未知 transform 冷启动。");
        Assert.Equal(MapScaleSearchPolicy.Search, route.ScaleSearchPolicy);
        Assert.False(
            route.RestrictSearchToLockedTransform,
            "未知冷启动不得把平移盆地钉在 (0,0)。");
        Assert.False(
            route.TrackingMode,
            "未知冷启动不得获得 tracking 的窄窗权限。");
    }

    // ── A. 未知冷启动必须是真正的全尺度搜索 ──────────────────────────────────

    [Fact]
    public void PendingVariantNeutralSeedMustSearchTheWholeScaleRange()
    {
        var map = CreateMap();
        var session = CreatePendingVariantPrimaryFloorSession(map);

        var route = ResolveRoute(session.LockedTransform);

        AssertUnknownTransformColdStart(route);

        // 只把 Fixed 换成 Search 是不够的：普通配置的 ScaleSearchRadius 默认只有
        // 0.02，围绕 neutral 1.0 的 ±2% 依然是注定失败的局部搜索。
        var defaultRadius = new MapStructureRegistrationTuning().ScaleSearchRadius;
        var tuning = new MapStructureRegistrationTuning();
        MapOpenAlignmentRouteRules.ApplyUnknownScaleGlobalRecoveryPolicy(
            tuning,
            hasCalibration: false);

        Assert.True(
            tuning.ScaleSearchRadius > defaultRadius,
            $"未知冷启动半径必须显著大于默认局部半径 {defaultRadius}。");
        Assert.Equal(0d, tuning.TrackingScaleSearchRadius);
        Assert.True(tuning.DisableScaleEarlyTermination);
        Assert.False(tuning.EnableFastAlignment);

        // neutral 1.0 ± 半径必须覆盖未标定全局区间约 0.30 ~ 1.70。
        Assert.True(
            1d - tuning.ScaleSearchRadius
                <= MapFloorScaleSearchPolicy.UncalibratedMinimumScale + 1e-9,
            $"下界 {1d - tuning.ScaleSearchRadius:F4} 未覆盖全局最小值。");
        Assert.True(
            1d + tuning.ScaleSearchRadius
                >= MapFloorScaleSearchPolicy.UncalibratedMaximumScale - 1e-9,
            $"上界 {1d + tuning.ScaleSearchRadius:F4} 未覆盖全局最大值。");

        // 未标定恢复半径是既有的、已被验证的值，冷启动必须复用它而不是另起一套。
        Assert.Equal(
            MapOpenAlignmentRouteRules.ResolveSingleGlobalRecoveryRadius(false),
            tuning.ScaleSearchRadius);
    }

    [Fact]
    public void NeutralSeedIsNeverAcceptedAsScaleOneEvidence()
    {
        var map = CreateMap();
        var session = CreatePendingVariantPrimaryFloorSession(map);

        // 中性种子本身只满足对象契约，不携带任何尺度证据。
        Assert.Equal(1d, session.LockedTransform.ScaleX);
        Assert.Equal(0d, session.LockedTransform.OffsetX);
        Assert.Equal(0d, session.LockedTransform.OffsetY);

        var route = ResolveRoute(session.LockedTransform);
        Assert.NotEqual(MapScaleSearchPolicy.Fixed, route.ScaleSearchPolicy);
    }

    // ── B. 变体切换不得让主楼层失去双门能力 ──────────────────────────────────

    [Fact]
    public void PendingVariantPrimaryFloorStillRunsGateDetection()
    {
        var map = CreateMap();
        var session = CreatePendingVariantPrimaryFloorSession(map);

        // 刚切换过来的变体没有门对锁定，必须继续门检测，否则无法求解尺度。
        Assert.False(
            MapOpenAlignmentRouteRules.CanSkipGateDetectionForLockedGatePair(
                SelectedAlignmentRoute.Default,
                session,
                hasAlignmentDeadline: true));

        // 主楼层路由仍然是 Default，双门可见时依旧走 AlignSelectedWithGatePair。
        Assert.Equal(
            SelectedAlignmentRoute.Default,
            MapOpenAlignmentRouteRules.ResolvePendingIdentityRoute(session));

        // 不能因为 pending variant 就被粗暴改判成"独立楼层/无门路线"。
        Assert.False(
            MapOpenAlignmentRouteRules.ShouldUseIndependentFloorAlignment(
                isOtherFloor: false,
                isPendingVariantAlignment: true,
                session));
    }

    [Fact]
    public void OnlyACommittedGatePairLockMaySkipGateDetection()
    {
        var map = CreateMap();
        var lockedSession = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = "1f",
            LockedTransform = MeasuredTransform(0.736d),
            BaselineGateScale = 0.736d,
            HasGatePairLock = true,
            Mode = MapAlignmentTrackingMode.GatePairLocked
        };

        Assert.True(
            MapOpenAlignmentRouteRules.CanSkipGateDetectionForLockedGatePair(
                SelectedAlignmentRoute.Default,
                lockedSession,
                hasAlignmentDeadline: true));
        // 没有对齐预算时同样不跳过：这不是"有锁定就一定省一次检测"的捷径。
        Assert.False(
            MapOpenAlignmentRouteRules.CanSkipGateDetectionForLockedGatePair(
                SelectedAlignmentRoute.Default,
                lockedSession,
                hasAlignmentDeadline: false));
    }

    // ── C. 真实证据必须立刻恢复 Fixed / Restricted / Tracking 权限 ────────────

    [Fact]
    public void SingleGateEvidenceUpgradesTheSeedToRealEvidence()
    {
        var measuredSeed = MeasuredTransform(0.736d);
        Assert.False(MapFloorScaleSeedRules.IsNeutralIndependentSeed(measuredSeed));

        var route = ResolveRoute(
            measuredSeed,
            hasFreshAnchorTransform: true,
            hasSingleGateProposal: true,
            restrictStructureSearch: true);

        Assert.Equal(MapStructureSeedKind.Anchor, route.SeedKind);
        Assert.False(
            route.UnknownTransformColdStart,
            "本帧单门测量是真实证据，不属于未知冷启动。");
        Assert.Equal(MapScaleSearchPolicy.Fixed, route.ScaleSearchPolicy);
        Assert.True(route.RestrictSearchToLockedTransform);
        Assert.True(route.TrackingMode);
    }

    [Fact]
    public void ReliableSessionSeedKeepsItsExistingLocalBasin()
    {
        var sessionSeed = MeasuredTransform(0.736d);

        // 没有任何本帧测量时，非中性种子来自已采纳的会话/缓存变换——那是有证据的。
        var route = ResolveRoute(sessionSeed);

        Assert.Equal(MapStructureSeedKind.Session, route.SeedKind);
        Assert.False(route.UnknownTransformColdStart);
        Assert.Equal(MapScaleSearchPolicy.Fixed, route.ScaleSearchPolicy);
    }

    [Fact]
    public void ScanVerificationGateIsNotRelaxedByTheColdStartPolicy()
    {
        var map = CreateMap();
        var session = CreatePendingVariantPrimaryFloorSession(map);

        // ScanVerification 有自己的固定尺度校验闸门。即使它因故拿到中性种子，
        // 也不得被冷启动策略放宽成宽半径搜索或取消受限搜索。
        var route = ResolveRoute(
            session.LockedTransform,
            isScanVerification: true);

        Assert.False(route.UnknownTransformColdStart);
        Assert.Equal(MapScaleSearchPolicy.Fixed, route.ScaleSearchPolicy);
        Assert.True(route.RestrictSearchToLockedTransform);
    }

    // ── 完整状态回归：A 对齐成功 → 切到 B → 清状态 → B 冷启动 → 切回 A ──────

    [Fact]
    public void SwitchingVariantsNeverLeavesAFloorStuckOnNeutralFixedScale()
    {
        var mapA = CreateMap();
        var mapB = CreateMap();

        // A 已经对齐成功：真实尺度，门对锁定。
        var alignedA = new MapAlignmentSession
        {
            MapId = mapA.Id,
            MapUpdatedAt = mapA.UpdatedAt,
            FloorKey = "1f",
            LockedTransform = MeasuredTransform(0.736d),
            BaselineGateScale = 0.736d,
            HasGatePairLock = true,
            LastConfidence = 0.91d,
            Mode = MapAlignmentTrackingMode.GatePairLocked
        };

        // A -> B：切换变体会清空会话状态。即使把旧的 A 变换当作 previous 递进来，
        // 地图身份不匹配也必须拦住跨变体复用。
        var sessionB = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            mapB,
            TransformlessResult(mapB, "1f"),
            pendingSideEntranceSeed: null,
            previous: alignedA,
            canReusePrevious: true,
            targetFloorKey: "1f");

        Assert.NotSame(alignedA, sessionB);
        Assert.Equal(mapB.Id, sessionB.MapId);
        Assert.True(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(sessionB.LockedTransform),
            "B 必须从自己的中性种子冷启动，不得继承 A 的尺度。");
        Assert.NotEqual(0.736d, sessionB.LockedTransform.ScaleX);

        var routeB = ResolveRoute(sessionB.LockedTransform);
        AssertUnknownTransformColdStart(routeB);

        // B 无门、VPSG 不接受 → 全局结构搜索。搜索契约本身不得允许
        // "Fixed + scaleSeed=1" 这种永久停在错尺度上的组合。
        var searchTuning = new MapStructureRegistrationTuning();
        MapOpenAlignmentRouteRules.ApplyUnknownScaleGlobalRecoveryPolicy(
            searchTuning,
            hasCalibration: false);
        Assert.Equal(MapScaleSearchPolicy.Search, routeB.ScaleSearchPolicy);
        Assert.True(
            1d + searchTuning.ScaleSearchRadius
                >= MapFloorScaleSearchPolicy.UncalibratedMaximumScale - 1e-9);

        // B -> A：状态同样已被清空，所以 A 必须像第一次那样重新冷启动。
        // 之前切过变体不得让 A 失去尺度搜索能力。
        var sessionA = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            mapA,
            TransformlessResult(mapA, "1f"),
            pendingSideEntranceSeed: null,
            previous: null,
            canReusePrevious: false,
            targetFloorKey: "1f");

        Assert.Equal(mapA.Id, sessionA.MapId);
        Assert.True(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(sessionA.LockedTransform));
        AssertUnknownTransformColdStart(ResolveRoute(sessionA.LockedTransform));
    }

    [Fact]
    public void SameVariantPreviousTransformStaysReusable()
    {
        var map = CreateMap();
        var aligned = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = "1f",
            LockedTransform = MeasuredTransform(0.736d),
            BaselineGateScale = 0.736d,
            HasGatePairLock = true,
            LastConfidence = 0.91d
        };

        // 正向对照：同一张地图的既有变换属于合法复用，冷启动防线不应误伤它。
        var reused = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            map,
            TransformlessResult(map, "1f"),
            pendingSideEntranceSeed: null,
            previous: aligned,
            canReusePrevious: true,
            targetFloorKey: "1f");

        Assert.Same(aligned, reused);
        Assert.False(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(reused.LockedTransform));
    }

    // ── D. 结构-only 入口（AlignStructureOnly）上的同一份契约 ─────────────────
    //
    // 该入口被 AlignWithCachedScale 等调用方以写死的 Fixed 复用。实测日志里
    // 「scaleSearchPolicy=Fixed | scaleSeed=1 | scaleHypotheses=1」正是从这里
    // 出来的（registrar 的 baselineScale 只是 seed 除以 physical/computation
    // 比例，所以 scaleSeed=1 对应 baselineScale≈0.7564）。断言纯规则而不是
    // AlignStructureOnly 内部的分支。

    [Fact]
    public void NeutralSeedFromACachedScaleEntryIsNotFrozenAtScaleOne()
    {
        var map = CreateMap();
        var neutralSeed = CreatePendingVariantPrimaryFloorSession(map).LockedTransform;

        // 调用方写死 Fixed，但它手里只有中性占位符：必须降级为全尺度搜索。
        var resolved = MapOpenAlignmentRouteRules.ResolveStructureOnlyScaleSearchPolicy(
            MapScaleSearchPolicy.Fixed,
            neutralSeed,
            isScanVerification: false);

        Assert.Equal(MapScaleSearchPolicy.Search, resolved);

        // 降级后必须同时拿到真正的全局半径，而不是默认的 ±0.02。
        var tuning = new MapStructureRegistrationTuning();
        MapOpenAlignmentRouteRules.ApplyUnknownScaleGlobalRecoveryPolicy(
            tuning,
            hasCalibration: false);
        Assert.True(
            1d + tuning.ScaleSearchRadius
                >= MapFloorScaleSearchPolicy.UncalibratedMaximumScale - 1e-9);
        Assert.False(tuning.EnableFastAlignment);
    }

    [Fact]
    public void RealCachedScaleKeepsItsFixedValidation()
    {
        // 正向对照：缓存/VPSG 候选给出的是真实测量（非中性），Fixed 验证必须保留，
        // 冷启动防线不得把已经很贵的固定尺度校验升级成全局搜索。
        var measuredSeed = MeasuredTransform(0.736d);
        Assert.False(MapFloorScaleSeedRules.IsNeutralIndependentSeed(measuredSeed));

        Assert.Equal(
            MapScaleSearchPolicy.Fixed,
            MapOpenAlignmentRouteRules.ResolveStructureOnlyScaleSearchPolicy(
                MapScaleSearchPolicy.Fixed,
                measuredSeed,
                isScanVerification: false));
    }

    [Fact]
    public void ScanVerificationDoesNotGetTheNeutralSeedSeal()
    {
        var map = CreateMap();
        var neutralSeed = CreatePendingVariantPrimaryFloorSession(map).LockedTransform;

        // 扫描校验有自己的固定尺度闸门，即使因故拿到中性种子也不得被放宽。
        Assert.Equal(
            MapScaleSearchPolicy.Fixed,
            MapOpenAlignmentRouteRules.ResolveStructureOnlyScaleSearchPolicy(
                MapScaleSearchPolicy.Fixed,
                neutralSeed,
                isScanVerification: true));
    }

    [Fact]
    public void TheSealNeverUpgradesASearchRequestOrSilentlyDowngrades()
    {
        var measuredSeed = MeasuredTransform(0.736d);

        // Search 请求原样透传，不会被这条规则改写。
        Assert.Equal(
            MapScaleSearchPolicy.Search,
            MapOpenAlignmentRouteRules.ResolveStructureOnlyScaleSearchPolicy(
                MapScaleSearchPolicy.Search,
                measuredSeed,
                isScanVerification: false));
        Assert.Equal(
            MapScaleSearchPolicy.Search,
            MapOpenAlignmentRouteRules.ResolveStructureOnlyScaleSearchPolicy(
                MapScaleSearchPolicy.Search,
                measuredSeed,
                isScanVerification: true));
    }
}
