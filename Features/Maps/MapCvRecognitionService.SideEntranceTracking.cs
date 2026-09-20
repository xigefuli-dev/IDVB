using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private const double LockedSideEntranceMinimumScore = 0.80d;
    private const double LockedSideEntranceMaximumScaleChangeRatio = 0.08d;

    /// <summary>
    /// Re-locates one already locked primary floor from its authored side
    /// feature. No other map participates, so this path cannot alter ranking
    /// or identity. A high feature score and a scale consistent with the
    /// existing lock only produce a same-frame transform proposal; static
    /// structure must independently validate it before it can be committed.
    /// </summary>
    public MapRecognitionAttempt AlignLockedSideEntranceFeature(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(session);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);

        var stopwatch = Stopwatch.StartNew();
        var candidate = RunSideEntranceScan(
                frame.Image,
                topK: 1,
                selectedMapId: selectedMapId)
            .FirstOrDefault(item => string.Equals(
                item.FloorKey,
                session.FloorKey,
                StringComparison.Ordinal));
        stopwatch.Stop();
        if (candidate is null)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map side feature was not visible.");

        var baselineScale = session.BaselineGateScale;
        var scaleChange = double.IsFinite(baselineScale) && baselineScale > 0d
            ? Math.Abs((candidate.MatchScale / baselineScale) - 1d)
            : double.PositiveInfinity;
        var minimumScore = Math.Max(
            LockedSideEntranceMinimumScore,
            tuning.MinimumConfidence);
        if (candidate.MatchScore < minimumScore
            || scaleChange > LockedSideEntranceMaximumScaleChangeRatio)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map side feature observation was not strong enough to seed structure validation.");
        }

        if (!SideEntranceScanPipeline.TryCreateAlignmentSeed(
                candidate,
                frame.ViewportBounds,
                out var seed,
                out var seedFailure))
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                seedFailure);
        }

        MapLogCollector.Instance.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"已锁定地图侧门特征提出结构验证种子 · score={candidate.MatchScore:P0} · "
            + $"scale={candidate.MatchScale:F3}",
            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = selectedMapId,
                ["floor"] = session.FloorKey,
                ["matchScore"] = candidate.MatchScore,
                ["matchScale"] = candidate.MatchScale,
                ["scaleChange"] = scaleChange
            });
        var searchContext = CreateSideEntranceWarmSearchContext(
            seed,
            tuning,
            useInitialHighPrecisionRecovery: false,
            useLockedFixedStructureValidation: true);
        return AlignSideEntrance(
            frame,
            selectedMapId,
            seed,
            alignmentMode,
            tuning,
            structureTuning,
            alignmentSearchContext: searchContext);
    }
}
/*
 * 文件职责：MapCvRecognitionService.SideEntranceTracking。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
