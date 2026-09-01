// IDVB Remaster — Session Orchestrator（新架构唯一入口）

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Survey.Contracts;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{

    private async Task EndMatchAsync(bool saveAutomaticMapCache)
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            var endingMatch = _matchSession.Snapshot;
            if (!endingMatch.IsStarted)
                return;
            var finalLearningMapId = _mapLease.MapId
                ?? _pendingAlignmentIdentity?.Map.Id
                ?? _lastRecognition?.Map.Id;

            if (_matchPluginsActivated)
                await SetMatchPluginsActivatedCoreAsync(false);

            Volatile.Write(ref _matchEnding, 1);
            // Invalidate the match identity first. Any scan/alignment already
            // running may finish native work, but can no longer commit state.
            _matchSession.End();
            MapDiagnosticModeCapture.EndMatch();
            CancelMatchOperations();
            var isSurvey = endingMatch.Mode == MapRunMode.Survey;
            _statusMessage = isSurvey
                ? "正在结束测绘对局并保存测绘项目……"
                : saveAutomaticMapCache
                    ? "正在结束对局并保存地图缓存……"
                    : "正在结束对局并丢弃本局地图缓存样本……";
            StateChanged?.Invoke(this, EventArgs.Empty);

            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            await DrainAdaptiveScaleAsync();
            if (!isSurvey && saveAutomaticMapCache)
                await FlushAutomaticMapCacheAsync();
            else
                DiscardAutomaticMapCacheSamples(isSurvey
                    ? "测绘对局不使用普通地图缓存样本"
                    : "用户选择不保存或退出路径无法确认");
            await EndSurveyMatchAsync(endingMatch);
            await DrainHumanMapSelectionRecordingAsync();
            if (!isSurvey)
                await FinalizeMapLearningLabelAsync(
                    endingMatch,
                    finalLearningMapId);
            if (_hasPendingMapLearningSample
                && _settings?.ContinuousMapLearningEnabled is true
                && _settings.AutomaticMapModelTrainingEnabled)
            {
                QueueMapModelTraining();
            }
            ResetMatchTransientState(resetAutomaticCacheSamples: true);

            _statusMessage = "对局已结束。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"退出对局完成 · version={endingMatch.Version} · "
                + (isSurvey
                    ? "测绘项目已保存，普通地图缓存样本已静默丢弃"
                    : saveAutomaticMapCache
                    ? "本局任务已排空，自动地图缓存已完成确认落盘阶段"
                    : "本局任务已排空，自动地图缓存样本未保存"));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _matchEnding, 0);
            _matchLifecycleGate.Release();
        }
    }

    private async Task SetMatchPluginsActivatedCoreAsync(bool active)
    {
        var handlers = MatchPluginActivationChanged;
        if (handlers is not null)
        {
            foreach (Func<bool, Task> handler in handlers.GetInvocationList())
                await handler(active);
        }
        _matchPluginsActivated = active;
    }

    /// <summary>
    /// 用最新识别结果重建对齐会话。侧门扫描锁定的结果来源是
    /// <see cref="MapRecognitionSource.StructureMatching"/>（而非
    /// SideEntranceSelection），直接从结果重建会丢失侧门先验并错误地设置
    /// HasGatePairLock=true。这里在识别结果自然更新会话的同时，保留上一会话
    /// 的侧门身份先验，使后续"仅对齐"仍走允许缩放搜索的侧门路线。
    /// </summary>
    private static MapAlignmentSession UpdateAlignmentSession(
        MapAlignmentSession? previous,
        RuntimeMapRecognition recognition) =>
        MapAlignmentSession.RebuildPreservingFirstScanIdentity(
            previous,
            recognition.Map,
            recognition.Result);

    private void RememberPrimaryFloorSession(
        RuntimeMapRecognition recognition,
        MapAlignmentSession? session)
    {
        if (session is null)
            return;
        if (string.Equals(
                recognition.Result.Floor,
                MapFloorRules.GetPrimaryFloorKey(recognition.Map),
                StringComparison.Ordinal))
        {
            _primaryFloorAlignmentSession = session;
            RememberAdaptiveReliableKey(recognition, primary: true);
        }
    }

}
