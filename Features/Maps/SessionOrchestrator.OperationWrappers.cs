using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunMapOpenAlignmentAsync(
        MapGameToggleTransition toggle)
    {
        var mapOpenCancellation = BeginMapOpenCancellationScope();
        var cancellationToken = mapOpenCancellation.Token;
        CancelOrbTracking("absolute alignment started");
        await DrainOrbTrackingAsync();
        var operationMatch = _matchSession.Snapshot;
        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有识别正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            CompleteMapOpenCancellationScope(mapOpenCancellation);
            return;
        }

        var trace = BeginMapOperationTrace(
            MapOperationTypes.MapOpenAlignment,
            AlignmentTracePhases);
        var outcome = "success";
        var terminalReason = "completed";
        var traceFinished = false;
        var restoreMainContent = false;
        try
        {
            using (trace.StartTopLevel("route_prepare"))
            {
                // The wrapper owns the gate and overlay transition. Keeping
                // this span explicit prevents that orchestration time from
                // becoming an unexplained prefix before alignment starts.
                restoreMainContent = _overlay.IsVisible;
                if (restoreMainContent)
                    _overlay.SetMainContentVisible(false);
            }
            await RunMapOpenAlignmentCoreAsync(
                toggle,
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"仅对齐已取消 · matchVersion={operationMatch.Version}");
            outcome = "cancelled";
            terminalReason = "match-cancellation";
        }
        catch (Exception ex)
        {
            outcome = "failed";
            terminalReason = $"exception:{ex.GetType().Name}";
            throw;
        }
        finally
        {
            try
            {
                using (trace.StartTopLevel("cleanup"))
                {
                    if (restoreMainContent
                        && IsCurrentMatchOperation(operationMatch)
                        && _gameMapToggleState.IsCurrent(toggle)
                        && !_overlay.HasMap)
                    {
                        _overlay.SetMainContentVisible(true);
                    }
                    _scanGate.Release();
                }
            }
            finally
            {
                CompleteMapOpenCancellationScope(mapOpenCancellation);
                if (!traceFinished)
                {
                    FinishMapOperationTrace(
                        trace,
                        isAlignment: true,
                        outcome,
                        terminalReason);
                    traceFinished = true;
                }
            }
        }
    }

    private async Task RunRecognitionPipelineAsync()
    {
        CancelOrbTracking("recognition scan started");
        await DrainOrbTrackingAsync();
        var operationMatch = _matchSession.Snapshot;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有扫描正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var trace = BeginMapOperationTrace(
            _settings!.BackgroundScanEnabled
                ? MapOperationTypes.BackgroundScan
                : MapOperationTypes.QuickScan,
            QuickScanTracePhases);
        var outcome = "success";
        var terminalReason = "completed";
        var traceFinished = false;
        try
        {
            using (trace.StartTopLevel("route_prepare"))
                UnlockMapForRescan();
            await RunRecognitionPipelineCoreAsync(
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"快捷扫描已取消 · matchVersion={operationMatch.Version}");
            outcome = "cancelled";
            terminalReason = "match-cancellation";
        }
        catch (Exception ex)
        {
            outcome = "failed";
            terminalReason = $"exception:{ex.GetType().Name}";
            throw;
        }
        finally
        {
            try
            {
                using (trace.StartTopLevel("cleanup"))
                {
                    _scanGate.Release();
                }
            }
            finally
            {
                if (!traceFinished)
                {
                    FinishMapOperationTrace(
                        trace,
                        isAlignment: false,
                        outcome,
                        terminalReason);
                    traceFinished = true;
                }
            }
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.OperationWrappers。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
