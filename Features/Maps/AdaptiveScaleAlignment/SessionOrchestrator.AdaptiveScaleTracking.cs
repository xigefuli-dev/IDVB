using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunAdaptiveStructureTrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        OrbTrackingConfig config,
        CancellationToken cancellationToken)
    {
        var current = recognition;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && IsOrbTrackingContextCurrent(context))
            {
                var interval = GetAdaptiveStructureProbeInterval(
                    context,
                    Math.Max(250, config.StructureCorrectionIntervalMs));
                await Task.Delay(interval, cancellationToken);
                if (!IsOrbTrackingContextCurrent(context))
                    break;
                if (!_captureSvc.TryCaptureViewport(
                        ResolveMapViewportForCurrentWindow(),
                        out var frameObject,
                        out _)
                    || frameObject is not CapturedGameFrame frame)
                {
                    continue;
                }

                using (frame)
                {
                    var corrected = TryCorrectOrbTrackingWithStructure(
                        context,
                        frame,
                        current,
                        config.MaximumBaselineScaleChangeRatio,
                        context.BaselineScale);
                    if (corrected is null)
                    {
                        NotifyAdaptiveStructureFailure(context);
                        continue;
                    }
                    var decision = EvaluateAdaptiveStructure(
                        context,
                        frame,
                        corrected,
                        Interlocked.Increment(ref _adaptiveFrameId));
                    current = decision.Recognition;
                    if (current.Result.OverlayTransform is not null)
                    {
                        EnqueueOrbTrackingCommit(
                            context,
                            current,
                            config.MaximumBaselineScaleChangeRatio);
                        if (decision.BecameReliable)
                            PublishAdaptiveReliableStatus(context, frame, current);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "Adaptive structure tracking stopped after an unexpected failure.",
                details: new()
                {
                    ["generation"] = context.Generation,
                    ["exception"] = exception.ToString()
                });
        }
        finally
        {
            lock (_orbTrackingGate)
            {
                if (_orbTrackingGeneration == context.Generation)
                {
                    _orbTrackingCancellation?.Dispose();
                    _orbTrackingCancellation = null;
                    _orbTrackingTask = null;
                }
            }
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleTracking。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
