using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void ShowAdaptiveProvisionalStatus(
        RuntimeMapRecognition recognition,
        AdaptiveAlignmentDecision decision,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle)
    {
        _statusMessage =
            $"临时对齐：{recognition.Map.DisplayName} · "
            + $"{recognition.Result.Floor.ToUpperInvariant()} · "
            + $"置信度 {recognition.Result.LocalizationConfidence:P0} · "
            + $"连续高质量 {decision.ConsecutiveHighQualityCount}/"
            + $"{decision.RequiredHighQualityCount}";
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Warning,
            "临时对齐",
            _statusMessage,
            $"置信度 {recognition.Result.LocalizationConfidence:P0} · "
            + $"连续高质量 {decision.ConsecutiveHighQualityCount}/"
            + $"{decision.RequiredHighQualityCount}",
            gameBounds,
            gameWindowHandle);
    }

    private void ShowAdaptiveReliableStatus(
        RuntimeMapRecognition recognition,
        AdaptiveAlignmentDecision decision,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle)
    {
        if (decision.ReliabilityReason == AdaptiveScaleReliabilityReason.Disabled)
        {
            ShowTransientAlignmentSuccess(recognition, gameBounds, gameWindowHandle);
            return;
        }
        var confidence = $"置信度 {recognition.Result.LocalizationConfidence:P0}";
        var detail = decision.ReliabilityReason switch
        {
            AdaptiveScaleReliabilityReason.InitialFiveStreak =>
                $"{confidence} · 连续高质量 "
                + $"{decision.ConsecutiveHighQualityCount}/{decision.RequiredHighQualityCount}",
            AdaptiveScaleReliabilityReason.StructureConsensus =>
                $"{confidence} · 多帧结构共识",
            _ => $"{confidence} · 已验证启动标定"
        };
        _statusMessage =
            $"可靠对齐：{recognition.Map.DisplayName} · "
            + $"{recognition.Result.Floor.ToUpperInvariant()} · {detail}";
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Success,
            "可靠对齐",
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}",
            detail,
            gameBounds,
            gameWindowHandle);
    }

    private void PublishAdaptiveReliableStatus(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition)
    {
        var gameBounds = frame.ClientBounds;
        var gameWindowHandle = frame.WindowHandle;
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (!IsOrbTrackingContextCurrent(context))
                return;
            _statusMessage =
                $"可靠对齐：{recognition.Map.DisplayName} · "
                + $"{recognition.Result.Floor.ToUpperInvariant()} · "
                + $"置信度 {recognition.Result.LocalizationConfidence:P0} · 多帧结构共识";
            ShowTransientOverlayStatus(
                MapOverlayStatusLevel.Success,
                "可靠对齐",
                $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}",
                $"置信度 {recognition.Result.LocalizationConfidence:P0} · 多帧结构共识",
                gameBounds,
                gameWindowHandle);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }))
        {
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "可靠对齐状态刷新未能加入 UI 队列。",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["confidence"] = recognition.Result.Confidence
                });
        }
    }
}
