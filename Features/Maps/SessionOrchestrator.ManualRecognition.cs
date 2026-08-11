// IDVB Remaster — Session Orchestrator（新架构唯一入口）
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{
    public async Task RunManualRecognitionAsync()
    {
        if (_disposed || !_settings!.IsEnabled)
            return;
        var operationMatch = _matchSession.Snapshot;
        if (!operationMatch.IsStarted || IsMatchEnding)
            return;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!_captureSvc.TryGetForegroundClientBounds(out _, out _, out _))
            return;

        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有扫描正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await RunManualRecognitionCoreAsync(
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别已取消 · matchVersion={operationMatch.Version}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// 手动识别：冻结游戏画面 → 弹窗框选大门/侧门 → 手动几何排名 →
    /// 若有歧义弹候选窗口供玩家选择 → 应用结果到 Overlay。
    /// 该链路恢复自旧 MapRuntimeService.ManualRecognition.cs 的完整交互。
    /// </summary>
    private async Task RunManualRecognitionCoreAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        // 冻结画面：捕获整个客户区，让玩家在拖框窗口内框选双门
        if (!_captureSvc.TryCaptureClient(out var frameObj, out _)
            || frameObj is not CapturedGameFrame frame)
        {
            _statusMessage = "手动识别截图失败，请保持游戏在前台并打开地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        using (frame)
        {
            var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(
                frame.ClientBounds,
                _settings!.ResolveMapViewportRegion(
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height))
                    ?? new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 });
            if (!viewportBounds.IsValid)
            {
                _statusMessage = "已校准的地图区域无效，请重新校准。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _statusMessage = "手动识别中……请框选大门和侧门。";
            StateChanged?.Invoke(this, EventArgs.Empty);

            ManualGateSelectionResult? selection;
            _manualSelectionActive = true;
            try
            {
                selection = await MapManualRecognitionWindow.ShowAsync(
                    frame,
                    viewportBounds,
                    cancellationToken);
            }
            finally
            {
                _manualSelectionActive = false;
            }

            if (selection is null)
            {
                _statusMessage = "已取消手动识别。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var attempt = await Task.Run(
                () => _recognition.RecognizeManual(
                    viewportBounds,
                    selection.MainGateBounds,
                    selection.SideGateBounds,
                    _settings.OverlayAlignmentMode,
                    _settings.RecognitionTuning.Clone(),
                    mapClass: operationMatch.MapClass));
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            _lastDiagnostics = attempt.Diagnostics;

            RuntimeMapRecognition? recognition = attempt.Recognition;
            if (recognition is null && attempt.Choices.Count > 0)
            {
                var decision = await MapManualCandidateWindow.ShowAsync(
                    frame,
                    attempt.Choices,
                    attempt.FailureReason,
                    cancellationToken);
                if (decision.Kind == MapCandidateDecisionKind.StartSurvey)
                {
                    await ActivateSurveyFromQuickScanAsync(
                        frame,
                        operationMatch,
                        cancellationToken);
                    return;
                }
                if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
                    || decision.CandidateIndex is not { } selectedIndex
                    || selectedIndex < 0
                    || selectedIndex >= attempt.Choices.Count)
                {
                    _statusMessage = "已取消候选确认。";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                recognition = MapCvRecognitionService.ConfirmChoice(
                    attempt.Choices[selectedIndex]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            if (recognition is null)
            {
                _statusMessage = $"手动识别失败：{attempt.FailureReason}";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            RecordResearchAttempt(
                recognition.Map,
                recognition.Result.Floor,
                frame,
                attempt,
                "manual-recognition",
                recognitionOverride: recognition);
            RecordSuccessfulAlignment(recognition, frame);
            await PersistPreprocessedScaleAsync(
                recognition,
                frame,
                attempt.Diagnostics);
            _lastRecognition = recognition;
            _lastAlignmentSession = UpdateAlignmentSession(
                _lastAlignmentSession,
                recognition);
            RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
            RememberReliableFloorAlignment(
                operationMatch,
                recognition,
                _lastAlignmentSession);
            _lastGameBounds = frame.ClientBounds;
            _lastGameWindowHandle = frame.WindowHandle;
            _statusMessage =
                $"手动识别：{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别完成 · map={recognition.Map.Id} · floor={recognition.Result.Floor}",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["confidence"] = recognition.Result.Confidence
                });
            _overlay.UpdateMap(
                recognition,
                frame.ClientBounds,
                frame.WindowHandle,
                _settings.ShowOverlayStatus);
            ShowTransientAlignmentSuccess(
                recognition,
                frame.ClientBounds,
                frame.WindowHandle,
                attempt.Diagnostics);
            _overlay.Show();
            RefreshMiniMapForCurrentFloor();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
