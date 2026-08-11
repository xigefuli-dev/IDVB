namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<bool> PublishMapOpenAlignmentResultAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool recoveringSelectedIdentity,
        RuntimeMapRecognition? aligned,
        string? failureReason,
        MapFeatureCacheKey? repairCacheKey)
    {
        // A background result must never overwrite a newer close/open action.
        if (!IsCurrentMatchOperation(operationMatch)
            || !_gameMapToggleState.IsCurrent(toggle))
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "仅对齐结果已丢弃（地图已关闭或重新打开）。");
            return false;
        }

        if (aligned is not null)
        {
            await RepairMapCacheAsync(repairCacheKey, aligned, frame);
            await PersistPreprocessedScaleAsync(
                aligned,
                frame,
                _lastDiagnostics);
            if (!IsCurrentMatchOperation(operationMatch))
                return false;

            RecordSuccessfulAlignment(aligned, frame);
            _lastRecognition = aligned;
            _pendingAlignmentIdentity = null;
            _pendingAlignmentSeed = null;
            var updatedSession = UpdateAlignmentSession(
                _lastAlignmentSession,
                aligned);
            _lastAlignmentSession = updatedSession;
            RememberPrimaryFloorSession(aligned, updatedSession);
            RememberReliableFloorAlignment(
                operationMatch,
                aligned,
                updatedSession);
            _lastGameBounds = frame.ClientBounds;
            _lastGameWindowHandle = frame.WindowHandle;
            _statusMessage =
                $"地图已对齐：{aligned.Map.DisplayName} · {aligned.Result.Floor.ToUpperInvariant()}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"仅对齐完成 · map={aligned.Map.Id} · floor={aligned.Result.Floor}",
                details: new()
                {
                    ["mapId"] = aligned.Map.Id,
                    ["floor"] = aligned.Result.Floor,
                    ["identityConfidence"] =
                        aligned.Result.IdentityConfidence,
                    ["localizationConfidence"] =
                        aligned.Result.LocalizationConfidence,
                    ["candidateMargin"] =
                        MapFeatureCacheRules.GetCandidateMargin(aligned.Result)
                });
            _overlay.UpdateMap(
                aligned,
                frame.ClientBounds,
                frame.WindowHandle,
                _settings!.ShowOverlayStatus);
            ShowTransientAlignmentSuccess(
                aligned,
                frame.ClientBounds,
                frame.WindowHandle,
                _lastDiagnostics);
            _overlay.Show();
            RefreshMiniMapForCurrentFloor();
            return true;
        }

        // A failed alignment clears the stale transform instead of presenting
        // the previous image as though the new observation had succeeded.
        var manualFloorLabel = MapFloorRules.GetFloorDisplayName(
            locked.Map,
            targetFloorKey);
        _statusMessage = recoveringSelectedIdentity
            ? $"所选地图暂未完成首次对齐：{locked.Map.DisplayName} · "
                + $"{failureReason ?? "无法匹配当前画面"}"
            : $"对齐未更新：当前按{manualFloorLabel}对齐；"
                + $"{failureReason ?? "无法匹配当前画面"}";
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Warning,
            $"仅对齐未更新 · map={locked.Map.Id} · reason={failureReason ?? "<none>"}");
        _overlay.ClearMap();
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Failure,
            "地图重新对齐失败",
            _statusMessage,
            recoveringSelectedIdentity
                ? "已保留所选地图身份；请保持完整地图打开并重新打开地图重试。"
                : "本次未复用旧变换；请保持完整地图打开，确认 IDVB 手动楼层正确后重新打开地图重试。",
            frame.ClientBounds,
            frame.WindowHandle);
        _overlay.Show();
        RefreshMiniMapForCurrentFloor();
        return true;
    }
}
