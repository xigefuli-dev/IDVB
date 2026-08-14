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
            var adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                aligned,
                frame,
                _lastDiagnostics);
            aligned = adaptiveDecision.RecognitionToRender;
            if (adaptiveDecision.AllowLegacyCacheWrite)
            {
                await RepairMapCacheAsync(repairCacheKey, aligned, frame);
                await PersistPreprocessedScaleAsync(
                    aligned,
                    frame,
                    _lastDiagnostics);
                RecordSuccessfulAlignment(aligned, frame);
            }
            if (!IsCurrentMatchOperation(operationMatch))
                return false;

            // 与识别管线一致：首次成功对齐时 _lastAlignmentSession 可能仍为
            // null，需回退到侧门扫描种子以保留 SideEntranceScanPriorConfidence；
            // 否则仅对齐成功后先验归零，后续重新对齐会退化到 Default 双门路线。
            var sideEntranceSeed = _pendingAlignmentSeed;
            _lastRecognition = aligned;
            _pendingAlignmentIdentity = null;
            _pendingAlignmentSeed = null;
            var updatedSession = UpdateAlignmentSession(
                _lastAlignmentSession ?? sideEntranceSeed,
                aligned);
            _lastAlignmentSession = updatedSession;
            if (adaptiveDecision.AllowReliableSession)
            {
                RememberPrimaryFloorSession(aligned, updatedSession);
                RememberReliableFloorAlignment(
                    operationMatch,
                    aligned,
                    updatedSession);
            }
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
            if (adaptiveDecision.AllowReliableSession)
            {
                ShowAdaptiveReliableStatus(
                    aligned,
                    adaptiveDecision,
                    frame.ClientBounds,
                    frame.WindowHandle);
            }
            else
            {
                ShowAdaptiveProvisionalStatus(
                    aligned,
                    adaptiveDecision,
                    frame.ClientBounds,
                    frame.WindowHandle);
            }
            _overlay.Show();
            if (adaptiveDecision.StartOrbTracking)
                await StartOrbTrackingAsync(aligned, frame);
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
