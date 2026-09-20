using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private bool TryGetAdaptiveScaleSeed(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out AdaptiveScaleSeedDecision? seed)
    {
        seed = null;
        if (!_adaptiveScale.Enabled)
            return false;
        var key = AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);
        return _adaptiveScale.TryGetPreferredSeed(
            key,
            _gameMapToggleState.Version,
            out seed);
    }

    private bool TryAlignWithAdaptiveCalibrationSeed(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        out AdaptiveScaleSeedDecision? seed,
        out MapRecognitionAttempt? attempt)
    {
        seed = null;
        attempt = null;
        var key = AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);
        if (!TryGetAdaptiveScaleSeed(frame, map, floorKey, out seed)
            || seed is null)
        {
            return false;
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        attempt = _recognition.AlignWithCachedScale(
            frame,
            map.Id,
            floorKey,
            MapFeatureCacheRules.CreateScaleSeed(map, floorKey, seed.Scale),
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence);
        timer.Stop();
        attempt.Diagnostics.ScaleSeedSource = seed.Source == AdaptiveScaleSeedSource.Runtime
            ? "adaptive-runtime"
            : "adaptive-calibration";
        attempt.Diagnostics.ScaleSeedScale = seed.Scale;
        attempt.Diagnostics.ScaleSeedTargetViewportWidth = key.ViewportWidth;
        attempt.Diagnostics.ScaleSeedTargetViewportHeight = key.ViewportHeight;
        attempt.Diagnostics.FinalValidatedScale =
            attempt.Recognition?.Result.OverlayTransform?.ScaleX ?? 0d;
        var qualityAccepted = IsAdaptiveInitialScaleQualified(
            attempt,
            structureTuning);
        if (!qualityAccepted)
        {
            attempt.Diagnostics.ScaleSeedRejectionReason =
                attempt.Recognition is not null
                    ? "adaptive-initial-quality-gate"
                    : string.IsNullOrWhiteSpace(attempt.StructureFailureReason)
                        ? attempt.FailureReason ?? "fixed-scale-validation-failed"
                        : attempt.StructureFailureReason;
        }

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            qualityAccepted ? MapLogLevel.Info : MapLogLevel.Warning,
            "自适应标定 seed 当前帧结构验证完成",
            elapsedMs: timer.Elapsed.TotalMilliseconds,
            details: new Dictionary<string, object?>
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["source"] = seed.Source.ToString(),
                ["scale"] = seed.Scale,
                ["client"] = $"{key.ClientWidth}x{key.ClientHeight}",
                ["viewport"] = $"{key.ViewportWidth}x{key.ViewportHeight}",
                ["accepted"] = qualityAccepted,
                ["failure"] = attempt.Diagnostics.ScaleSeedRejectionReason
            });
        return true;
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleSeed。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
