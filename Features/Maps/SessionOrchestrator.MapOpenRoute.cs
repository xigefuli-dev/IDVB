namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignMapOpenWithPreferredRoute(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool isOtherFloor,
        bool recoveringSelectedIdentity,
        MapAlignmentSession alignmentSession,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        Func<bool, MapRecognitionAttempt> fallback,
        out MapFeatureCacheKey? repairCacheKey)
    {
        repairCacheKey = null;

        // A low-structure floor has no gate evidence by definition. Keep this
        // guard at the route boundary as well as in the normal caller branch,
        // so a future route-selection change cannot send it through VPSG or a
        // side/double-gate fallback.
        if (MapAlignmentChannelRegistry.Resolve(
                locked.Map,
                targetFloorKey).Channel == MapAlignmentChannel.LowStructure)
        {
            return AlignExactManualFloor(
                frame,
                locked,
                targetFloorKey,
                MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                    locked.Map,
                    targetFloorKey),
                alignmentMode,
                tuning,
                structureTuning,
                alignmentSession.SideEntranceScanPriorConfidence,
                out repairCacheKey);
        }

        MapRecognitionAttempt VpsgThenFallback(bool tryDirectSideFeature)
        {
            // 无会话、无缓存时的 scale bootstrap：VPSG 用本楼层边缘结构
            // 独立估算 scale，成功则短路，失败再进入常规 fallback。
            if (TryAlignFloorWithVpsg(
                    frame,
                    locked,
                    targetFloorKey,
                    alignmentSession.LockedTransform,
                    alignmentMode,
                    tuning,
                    structureTuning,
                    alignmentSession.SideEntranceScanPriorConfidence)
                is { } vpsgAttempt
                && vpsgAttempt.Recognition is not null
                && IsAdaptiveInitialScaleQualified(vpsgAttempt, structureTuning))
            {
                return vpsgAttempt;
            }
            return fallback(tryDirectSideFeature);
        }

        // 侧门种子失败后的失败路径短路：本帧 AKAZE 结构验证刚拒绝过侧门特征，
        // 后续完整侧门路线（门检测提供 scale）实测 14/14 成功，而 VPSG（同一
        // AKAZE 特征源）在同类场景 15/15 失败、每次白付 150~267ms。因此交换
        // 优先级：先跑侧门完整路线；仅当它也失败才用 VPSG 的独立 scale 兜底，
        // 保留 AKAZE 在门模板尺度不可靠场景（如侧门扫描 scale 明显偏离）的
        // 独特价值，避免把注定失败的对齐做成最慢路径。
        MapRecognitionAttempt SideEntranceRouteThenVpsg(bool tryDirectSideFeature)
        {
            var directAttempt = fallback(tryDirectSideFeature);
            if (directAttempt.Recognition is not null)
                return directAttempt;
            if (TryAlignFloorWithVpsg(
                    frame,
                    locked,
                    targetFloorKey,
                    alignmentSession.LockedTransform,
                    alignmentMode,
                    tuning,
                    structureTuning,
                    alignmentSession.SideEntranceScanPriorConfidence)
                is { } vpsgAttempt
                && vpsgAttempt.Recognition is not null
                && IsAdaptiveInitialScaleQualified(vpsgAttempt, structureTuning))
            {
                return vpsgAttempt;
            }
            return directAttempt;
        }

        // 侧门特征只是为了给出一个 scale+位移提案。本帧已经有可信缩放种子时它
        // 提不出新信息，却要先跑一遍单地图多尺度模板扫描：实测 43/43 次主楼层
        // 对齐全部走到这里、全部在 score/scale 门被拒（连种子都没生成过一次），
        // 每次白付 p50 54ms。有可信种子就直接走缓存路线，侧门路线保留为它的
        // fallback——顺序变了，可达的证据来源一个没少。
        var hasTrustedScaleSeed = HasTrustedScaleSeed(
            frame,
            locked.Map,
            targetFloorKey);

        // 无论是否存在缩放种子，第一优先级优先尝试 VPSG 3.0 极速对齐（~30ms 轮廓几何求解）。
        // 成功则直接短路返回，避免白白耗费 200ms+ 执行侧门模板扫描与全量结构配准。
        if (TryAlignFloorWithVpsg(
                frame,
                locked,
                targetFloorKey,
                alignmentSession.LockedTransform,
                alignmentMode,
                tuning,
                structureTuning,
                alignmentSession.SideEntranceScanPriorConfidence)
            is { } fastVpsgAttempt
            && fastVpsgAttempt.Recognition is not null
            && IsAdaptiveInitialScaleQualified(fastVpsgAttempt, structureTuning))
        {
            var isVpsg3 = string.Equals(
                fastVpsgAttempt.Diagnostics.ScaleBootstrapMode,
                "Vpsg3",
                StringComparison.OrdinalIgnoreCase);
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                isVpsg3 ? "VPSG 3.0 首选快速对齐成功" : "VPSG 2.0 兜底对齐成功",
                details: new()
                {
                    ["route"] = isVpsg3 ? "preferred-vpsg3" : "vpsg2-fallback",
                    ["mapId"] = locked.Map.Id,
                    ["floor"] = targetFloorKey,
                    ["scale"] = fastVpsgAttempt.Recognition.Result.OverlayTransform?.ScaleX,
                    ["elapsedMs"] = fastVpsgAttempt.Diagnostics.TotalMilliseconds
                });
            return fastVpsgAttempt;
        }
        if (MapOpenAlignmentRouteRules.ShouldPreferLockedSideFeature(
                isOtherFloor,
                recoveringSelectedIdentity,
                alignmentSession.SideEntranceScanPriorConfidence)
            && !hasTrustedScaleSeed)
        {
            // A same-frame side feature provides a scale and translation
            // proposal. Static structure must validate that proposal before
            // it can be committed; the scale cache remains the fallback.
            // 已锁定地图场景用宽松质量门槛（RecoveryConfidence）：侧门扫描先验
            // 已给出强引导，0.73~0.81 的结构验证结果可直接采纳，避免被 0.82
            // 硬门槛拒绝后转缓存/完整恢复重复搜索（P0-2）。
            var directAttempt =
                _recognition.AlignLockedSideEntranceFeature(
                    frame,
                    locked.Map.Id,
                    alignmentSession,
                    alignmentMode,
                    tuning,
                    structureTuning);
            if (directAttempt.Recognition is not null
                && IsAdaptiveInitialScaleUsable(directAttempt, structureTuning))
                return directAttempt;

            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "主楼层侧门特征种子未通过结构验证，继续尝试缩放缓存与结构回退",
                details: new()
                {
                    ["route"] = "side-feature-structure-then-scale-cache",
                    ["reason"] = directAttempt.FailureReason
                });
            return AlignUsingScaleCache(
                frame,
                locked.Map,
                targetFloorKey,
                tuning,
                structureTuning,
                alignmentSession.SideEntranceScanPriorConfidence,
                () => SideEntranceRouteThenVpsg(false),
                out repairCacheKey);
        }

        // 上面因可信种子而跳过了侧门特征的场景，fallback 必须仍是侧门完整路线：
        // 该场景下 VPSG 与侧门是同一 AKAZE 特征源，实测侧门 14/14 成功而 VPSG
        // 15/15 失败（见 SideEntranceRouteThenVpsg 的说明）。只有本就不属于侧门
        // 身份的路线才用 VPSG 优先。
        var skippedSideFeatureForTrustedSeed = hasTrustedScaleSeed
            && MapOpenAlignmentRouteRules.ShouldPreferLockedSideFeature(
                isOtherFloor,
                recoveringSelectedIdentity,
                alignmentSession.SideEntranceScanPriorConfidence);
        if (skippedSideFeatureForTrustedSeed)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "已有可信缩放种子，跳过侧门特征提案，直接进入缩放缓存路线",
                details: new()
                {
                    ["route"] = "trusted-seed-skips-side-feature",
                    ["mapId"] = locked.Map.Id,
                    ["floor"] = targetFloorKey
                });
        }
        return AlignUsingScaleCache(
            frame,
            locked.Map,
            targetFloorKey,
            tuning,
            structureTuning,
            alignmentSession.SideEntranceScanPriorConfidence,
            // tryDirectSideFeature 保持 false：与"直接特征已试过并失败"后的原有
            // fallback 形状一致。实测直接特征 0/43 成功，缓存路线都失败的帧上更
            // 不会成功；完整侧门路线（门检测 + 结构配准）仍会跑，身份证据没丢。
            skippedSideFeatureForTrustedSeed
                ? () => SideEntranceRouteThenVpsg(false)
                : () => VpsgThenFallback(true),
            out repairCacheKey);
    }

    private void LogMapOpenAlignmentTimings(
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool isOtherFloor,
        bool succeeded,
        string? failureReason,
        double wallClockMilliseconds)
    {
        _lastAlignmentPhaseTimings = BuildAlignmentPhaseTimings(
            _lastDiagnostics,
            wallClockMilliseconds);
        var details = _lastAlignmentPhaseTimings.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value);
        details["mapId"] = locked.Map.Id;
        details["floor"] = targetFloorKey;
        details["route"] = isOtherFloor
            ? "structure-only-floor"
            : "primary-floor";
        details["succeeded"] = succeeded;
        details["failureReason"] = failureReason;
        details["targetP50Ms"] =
            MapOpenAlignmentRouteRules.TargetP50Milliseconds;
        details["targetP95Ms"] =
            MapOpenAlignmentRouteRules.TargetP95Milliseconds;
        details["maximumFailureMs"] =
            MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds;
        details["targetReliableRate"] =
            MapOpenAlignmentRouteRules.TargetReliableAlignmentRate;
        details["targetJitterP95Px"] =
            MapOpenAlignmentRouteRules.TargetTranslationJitterP95Pixels;
        _logCollector.Append(
            MapLogCategory.Session,
            succeeded ? MapLogLevel.Info : MapLogLevel.Warning,
            "仅对齐阶段耗时汇总",
            elapsedMs: wallClockMilliseconds,
            details: details);
    }
}
/*
 * 文件职责：SessionOrchestrator.MapOpenRoute。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
