using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private RuntimeMapRecognition? ProbeAdaptiveScaleStructure(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition predicted)
    {
        if (predicted.Result.OverlayTransform is not { } transform
            || _settings is null)
        {
            return null;
        }

        if (!IsAdaptiveScaleEnabled)
        {
            var legacyRadius = CreateEffectiveStructureTuning()
                .TrackingScaleSearchRadius;
            return RunAdaptiveFineScalePass(frame, predicted, legacyRadius)
                .Recognition;
        }

        var seed = predicted;
        if (AdaptiveScaleRequiresWideSearch(context))
        {
            var recoveryTuning = CreateEffectiveStructureTuning();
            recoveryTuning.ScaleSearchRadius = Math.Max(
                0.15d,
                recoveryTuning.ScaleSearchRadius);
            recoveryTuning.TrackingScaleSearchRadius = 0d;
            recoveryTuning.EnableFastAlignment = false;
            recoveryTuning.DisableScaleEarlyTermination = true;
            recoveryTuning.Normalize();
            var recovery = _recognition.AlignFloorWithoutGates(
                frame,
                predicted.Map.Id,
                predicted.Result.Floor,
                transform,
                MapOverlayAlignmentMode.Uniform,
                _settings.RecognitionTuning.Clone(),
                recoveryTuning,
                candidateHistory: null,
                isTracking: false,
                scaleSearchPolicy: MapScaleSearchPolicy.Search,
                identityPriorConfidence: predicted.Result.IdentityConfidence);
            if (recovery.Recognition is not { } recovered)
                return null;
            seed = recovered;
        }

        var probe = new AdaptiveScaleStructureProbe();
        var attempt = probe.Refine(
            seed,
            (passSeed, radius) => RunAdaptiveFineScalePass(
                frame,
                passSeed,
                radius));
        return attempt.Recognition;
    }

    private MapRecognitionAttempt RunAdaptiveFixedScaleTranslation(
        CapturedGameFrame frame,
        RuntimeMapRecognition latest,
        double consensusScale)
    {
        if (latest.Result.OverlayTransform is not { } transform)
            return new MapRecognitionAttempt { FailureReason = "missing-transform" };
        var fixedRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
            latest,
            AdaptiveScaleTransformArbitrator.KeepScale(transform, consensusScale),
            latest.Result.Source);
        var lockedSession = MapAlignmentSession.FromRecognition(
            fixedRecognition.Map,
            fixedRecognition.Result);
        return AlignNoDoorLocalStructure(
            frame,
            fixedRecognition,
            fixedRecognition.Result.Floor,
            lockedSession,
            MapOverlayAlignmentMode.Uniform,
            _settings!.RecognitionTuning.Clone(),
            CreateEffectiveStructureTuning(),
            [],
            fixedRecognition.Result.IdentityConfidence,
            allowTrackingScaleSearch: false);
    }

    private MapRecognitionAttempt RunAdaptiveFineScalePass(
        CapturedGameFrame frame,
        RuntimeMapRecognition seed,
        double radius)
    {
        var lockedSession = MapAlignmentSession.FromRecognition(
            seed.Map,
            seed.Result);
        var structureTuning = CreateEffectiveStructureTuning();
        structureTuning.TrackingScaleSearchRadius = radius;
        return AlignNoDoorLocalStructure(
            frame,
            seed,
            seed.Result.Floor,
            lockedSession,
            MapOverlayAlignmentMode.Uniform,
            _settings!.RecognitionTuning.Clone(),
            structureTuning,
            [],
            seed.Result.IdentityConfidence,
            allowTrackingScaleSearch: true);
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleProbe。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
