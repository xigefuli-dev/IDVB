namespace IDVBuff.Features.Maps;

internal static partial class MapOpenAlignmentRouteRules
{
    /// <summary>
    /// 应用「未知 scale 全局恢复」策略：seed scale 是未知的，必须做真正的
    /// 全尺度搜索，禁止固定 scale 粗搜索与单假设早停。
    /// <para>
    /// VPSG 失败后的全局恢复与中性种子冷启动共用这一份参数，避免两处各自
    /// 演进出不同的半径或早停设置。
    /// </para>
    /// </summary>
    internal static void ApplyUnknownScaleGlobalRecoveryPolicy(
        MapStructureRegistrationTuning tuning,
        bool hasCalibration)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        // 固定 SchemaVersion，防止 Normalize 按旧版本把开关强制翻回去。
        tuning.SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion;
        tuning.ScaleSearchRadius = ResolveSingleGlobalRecoveryRadius(hasCalibration);
        tuning.TrackingScaleSearchRadius = 0d;
        // seed scale 可能不准，禁止在错误 scale 上早停。
        tuning.DisableScaleEarlyTermination = true;
        // 走诚实的结构搜索，避免粗路径早退。
        tuning.EnableFastAlignment = false;
        tuning.Normalize();
    }

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

    internal static bool CanCompareMapOpenDrift(
        RuntimeMapRecognition previous,
        RuntimeMapRecognition current,
        string targetFloorKey) =>
        previous.Map.Id == current.Map.Id
        && previous.Map.UpdatedAt == current.Map.UpdatedAt
        && previous.Result.MapId == previous.Map.Id
        && current.Result.MapId == current.Map.Id
        && string.Equals(
            previous.Result.Floor,
            targetFloorKey,
            StringComparison.Ordinal)
        && string.Equals(
            current.Result.Floor,
            targetFloorKey,
            StringComparison.Ordinal);

    /// <summary>
    /// 判断结构配准种子是否属于「未知 transform 冷启动」：没有任何本帧或
    /// 会话级证据，只有一个满足对象契约的中性占位值。
    /// </summary>
    internal static MapStructureSeedKind ResolveStructureSeedKind(
        MapOverlayTransform structureSeed,
        bool hasFreshAnchorTransform,
        bool hasSingleGateProposal) =>
        MapFloorScaleSeedRules.IsNeutralIndependentSeed(structureSeed)
            ? MapStructureSeedKind.Neutral
            : hasFreshAnchorTransform || hasSingleGateProposal
                ? MapStructureSeedKind.Anchor
                : MapStructureSeedKind.Session;

    /// <summary>
    /// 解析结构 fallback 的搜索契约。
    /// <para>
    /// 中性种子是占位符而非证据，因此它既不能把尺度锁在 1.0（Fixed），
    /// 也不能把平移盆地钉在 (0,0)（RestrictSearchToLockedTransform），
    /// 更不能获得 tracking 的窄窗权限。任何会把中性种子升级成
    /// Fixed / Tracking / Restricted 的路径都必须在这里被封死，否则
    /// 「等待重新对齐」会退化成一次注定失败的局部搜索。
    /// </para>
    /// <para>
    /// ScanVerification 有自己的固定尺度校验闸门，且从不携带中性种子；
    /// 把它排除在冷启动策略之外，确保这条闸门不会被意外放宽。
    /// </para>
    /// </summary>
    internal static MapStructureSearchRoute ResolveStructureSearchRoute(
        MapOverlayTransform structureSeed,
        bool hasFreshAnchorTransform,
        bool hasSingleGateProposal,
        bool isScanVerification,
        bool isSideEntranceStructureRoute,
        bool restrictStructureSearch,
        AlignmentSearchContext? searchContext)
    {
        var seedKind = ResolveStructureSeedKind(
            structureSeed,
            hasFreshAnchorTransform,
            hasSingleGateProposal);
        var unknownTransformColdStart = !isScanVerification
            && seedKind == MapStructureSeedKind.Neutral
            && !hasFreshAnchorTransform
            && !hasSingleGateProposal;

        return new MapStructureSearchRoute(
            seedKind,
            unknownTransformColdStart,
            ScaleSearchPolicy: isScanVerification
                ? MapScaleSearchPolicy.Fixed
                : isSideEntranceStructureRoute
                    ? MapScaleSearchPolicy.Search
                    : unknownTransformColdStart
                        ? MapScaleSearchPolicy.Search
                        : MapScaleSearchPolicy.Fixed,
            RestrictStructureSearch: restrictStructureSearch,
            RestrictSearchToLockedTransform: !unknownTransformColdStart
                && (isScanVerification || restrictStructureSearch),
            TrackingMode: !unknownTransformColdStart
                && !isScanVerification
                && MapAlignmentSearchPolicy.UseTrackingForStructureValidation(
                    isSideEntranceStructureRoute,
                    searchContext));
    }

    /// <summary>
    /// 结构-only 入口（<c>AlignStructureOnly</c>）的尺度搜索策略闸门。
    /// <para>
    /// 该入口被缓存验证、VPSG 候选验证、无门楼层恢复等多条路线复用，其中若干
    /// 调用方会把 <see cref="MapScaleSearchPolicy.Fixed"/> 写死。一旦它手里拿到
    /// 的是中性占位种子（scale=1、tx=ty=0），Fixed 就等于宣称「尺度就是 1.0」，
    /// 只会围绕错误尺度造出唯一一个假设（scaleHypotheses=1）然后必然失败。
    /// </para>
    /// <para>
    /// 这里只在「调用方要 Fixed + seed 是中性占位符 + 不是扫描校验」时才降级为
    /// 全尺度搜索。带真实门/锚点/缓存/会话证据的路线 seed 不会是中性占位符，
    /// 因此完全不受影响。
    /// </para>
    /// </summary>
    internal static MapScaleSearchPolicy ResolveStructureOnlyScaleSearchPolicy(
        MapScaleSearchPolicy requestedPolicy,
        MapOverlayTransform structureSeed,
        bool isScanVerification) =>
        !isScanVerification
            && requestedPolicy == MapScaleSearchPolicy.Fixed
            && MapFloorScaleSeedRules.IsNeutralIndependentSeed(structureSeed)
                ? MapScaleSearchPolicy.Search
                : requestedPolicy;

    /// <summary>
    /// 解析结构 fallback 的搜索契约，并在属于未知 transform 冷启动时就地落实
    /// 未知尺度全局恢复参数（未标定全局区间约 0.30 ~ 1.70，而非默认的 ±0.02）。
    /// <para>
    /// 中性种子本身就意味着本楼层没有任何标定证据，故按未标定处理。
    /// </para>
    /// </summary>
    internal static MapStructureSearchRoute ApplyStructureSearchRoutePolicy(
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform structureSeed,
        bool hasFreshAnchorTransform,
        bool hasSingleGateProposal,
        bool isScanVerification,
        bool isSideEntranceStructureRoute,
        bool restrictStructureSearch,
        AlignmentSearchContext? searchContext,
        bool hasCalibration)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        var route = ResolveStructureSearchRoute(
            structureSeed,
            hasFreshAnchorTransform,
            hasSingleGateProposal,
            isScanVerification,
            isSideEntranceStructureRoute,
            restrictStructureSearch,
            searchContext);
        if (route.UnknownTransformColdStart)
            ApplyUnknownScaleGlobalRecoveryPolicy(tuning, hasCalibration);
        return route;
    }

    /// <summary>
    /// 在结构 fallback 请求创建前落盘本次搜索契约，便于事后判断
    /// 「等待重新对齐」究竟有没有真的做过全尺度搜索。
    /// </summary>
    internal static void LogStructureSearchRoute(
        MapStructureSearchRoute route,
        MapOverlayTransform structureSeed,
        MapStructureRegistrationTuning tuning,
        Guid mapId,
        string floorKey,
        bool isScanVerification,
        bool isInitialSideEntranceSeed,
        bool isSideEntranceStructureRoute,
        bool isSideEntranceRoute,
        bool useLockedFixedStructureValidation,
        int gateCount,
        bool hasSingleGateProposal,
        bool hasFreshAnchorTransform)
    {
        var routeLabel = isScanVerification
            ? "scan-verification"
            : isInitialSideEntranceSeed
                ? "initial-seed"
                : isSideEntranceStructureRoute
                    ? "tracking-repair"
                    : isSideEntranceRoute
                        ? useLockedFixedStructureValidation
                            ? "locked-fixed"
                            : "standard"
                        : "standard";
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构搜索契约 · {routeLabel}",
            details: new()
            {
                ["mapId"] = mapId,
                ["floor"] = floorKey,
                ["route"] = routeLabel,
                ["seedKind"] = route.SeedKind.ToString().ToLowerInvariant(),
                ["unknownTransformColdStart"] = route.UnknownTransformColdStart,
                ["scaleSearchPolicy"] = route.ScaleSearchPolicy.ToString(),
                ["scaleSeed"] = structureSeed.ScaleX,
                ["scaleSearchRadius"] = tuning.ScaleSearchRadius,
                ["trackingScaleSearchRadius"] = tuning.TrackingScaleSearchRadius,
                ["disableScaleEarlyTermination"] = tuning.DisableScaleEarlyTermination,
                ["enableFastAlignment"] = tuning.EnableFastAlignment,
                ["trackingMode"] = route.TrackingMode,
                ["restrictedSearch"] = route.RestrictSearchToLockedTransform,
                ["gateCount"] = gateCount,
                ["singleGateProposal"] = hasSingleGateProposal,
                ["freshAnchorTransform"] = hasFreshAnchorTransform
            });
    }

    /// <summary>
    /// 只有已提交的门对锁定加上真实的对齐预算，才允许主楼层路由整段跳过
    /// 门检测：门对已经解出尺度，重复检测只会白烧预算。
    /// <para>
    /// 刚切换过来的地图变体没有门对锁定，因此它必须继续做门检测，也就仍
    /// 然可以走双门路线。这条规则把「变体切换 = 直接走无门路线」挡在门外。
    /// </para>
    /// </summary>
    internal static bool CanSkipGateDetectionForLockedGatePair(
        SelectedAlignmentRoute route,
        MapAlignmentSession? session,
        bool hasAlignmentDeadline) =>
        route == SelectedAlignmentRoute.Default
        && session is { HasGatePairLock: true }
        && hasAlignmentDeadline;
}

/// <summary>
/// 结构配准种子来自哪里。中性种子只是满足对象契约的占位值，
/// 不携带任何尺度或平移证据。
/// </summary>
internal enum MapStructureSeedKind
{
    /// <summary>本楼层的独立中性种子（scale=1, tx=ty=0），无证据。</summary>
    Neutral = 0,
    /// <summary>本帧单门/锚点解出的变换，是本帧的真实测量。</summary>
    Anchor = 1,
    /// <summary>会话里已锁定的变换，是此前已采纳的证据。</summary>
    Session = 2
}

/// <summary>
/// 结构 fallback 的搜索契约：是否属于「未知 transform 冷启动」，
/// 以及由此决定的尺度搜索、受限搜索与 tracking 权限。
/// </summary>
internal readonly record struct MapStructureSearchRoute(
    MapStructureSeedKind SeedKind,
    bool UnknownTransformColdStart,
    MapScaleSearchPolicy ScaleSearchPolicy,
    bool RestrictStructureSearch,
    bool RestrictSearchToLockedTransform,
    bool TrackingMode);
/*
 * 文件职责：MapOpenAlignmentRouteRules.ColdStart。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：中性种子冷启动契约的集中解析处：seed 来源分类、尺度搜索策略、受限搜索与 tracking 权限。把这份契约从主规则文件拆出，既守住单文件长度约束，也让「占位符不是证据」这条规则只有一个定义点。
 * 数据流：输入为结构配准种子、门/锚点证据与会话上下文；输出为纯值对象，交给结构 fallback 与无门恢复路线消费。
 * 维护约束：这里只解析策略，不执行搜索；任何会放宽 VPSG3/ScanVerification 闸门的改动都必须先改这里的规则并补测试。
 */
