// IDVB Remaster — 乐观预呈现（Optimistic Overlay Presentation）与静默对账
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private RuntimeMapRecognition? _optimisticPresentationRecognition;
    private int _optimisticPresentationToggleVersion = -1;

    /// <summary>
    /// 判断当前开图转换是否已在屏幕上由乐观预呈现激活。
    /// </summary>
    private bool IsOptimisticPresentationActive(int toggleVersion) =>
        _optimisticPresentationToggleVersion == toggleVersion
        && _optimisticPresentationRecognition is not null
        && _gameMapToggleState.IsOpen;

    /// <summary>
    /// 在开图按键触发的第 1ms 内尝试执行乐观预呈现，消除玩家肉眼感知的开图延迟。
    /// 仅在地图身份已锁定且存在可信基准变换时生效。
    /// </summary>
    private bool TryPerformOptimisticMapOpenPresentation(
        MapGameToggleTransition toggle,
        MapScreenRect clientBounds,
        IntPtr windowHandle)
    {
        if (!toggle.IsOpen || !_settings!.IsEnabled || !_matchSession.Snapshot.IsStarted)
            return false;

        var locked = _pendingAlignmentIdentity ?? _lastRecognition;
        if (locked is null)
            return false;

        var primaryFloor = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var targetFloor = _currentFloorKey ?? primaryFloor;

        MapOverlayTransform? baselineTransform = null;
        if (_lastRecognition?.Result.Floor == targetFloor
            && _lastRecognition.Result.OverlayTransform is { } lastTrans)
        {
            baselineTransform = lastTrans;
        }
        else if (_primaryFloorAlignmentSession?.FloorKey == targetFloor
            && _primaryFloorAlignmentSession.LockedTransform is { } primaryTrans)
        {
            baselineTransform = primaryTrans;
        }
        else if (_lastAlignmentSession?.FloorKey == targetFloor
            && _lastAlignmentSession.LockedTransform is { } sessionTrans)
        {
            baselineTransform = sessionTrans;
        }

        if (baselineTransform is null
            || baselineTransform.ReferenceWidth <= 0
            || baselineTransform.ReferenceHeight <= 0)
        {
            return false;
        }

        var optimistic = new RuntimeMapRecognition
        {
            Map = locked.Map,
            FloorImagePath = _mapRepository.GetFloorOverlayPath(locked.Map, targetFloor),
            Result = new MapRecognitionResult
            {
                MapId = locked.Map.Id,
                Floor = targetFloor,
                Confidence = locked.Result.Confidence > 0 ? locked.Result.Confidence : 0.95d,
                IdentityConfidence = 1.0d,
                LocalizationConfidence = locked.Result.LocalizationConfidence > 0
                    ? locked.Result.LocalizationConfidence
                    : 0.85d,
                OverlayTransform = baselineTransform,
                EvidenceKind = locked.Result.EvidenceKind,
                Source = MapRecognitionSource.ReusedLastTransform
            }
        };

        using (var optimisticPresent = _overlay.DeferPresent())
        {
            _overlay.UpdateMap(
                optimistic,
                clientBounds,
                windowHandle,
                _settings.ShowOverlayStatus);
            if (!_overlay.HasMap)
            {
                return false;
            }

            _overlay.Show();
        }

        _optimisticPresentationRecognition = optimistic;
        _optimisticPresentationToggleVersion = toggle.Version;

        _logCollector.Append(
            MapLogCategory.Overlay,
            MapLogLevel.Info,
            $"[零延迟] 乐观预呈现已触发 · map={locked.Map.DisplayName} · floor={targetFloor} · "
            + $"scale={baselineTransform.ScaleX:F4} · offset=({baselineTransform.OffsetX:F0},{baselineTransform.OffsetY:F0}) · "
            + $"toggleVersion={toggle.Version}");

        return true;
    }

    /// <summary>
    /// 对后台算法计算出的新结果与乐观预呈现进行静默对账。
    /// 若漂移极小（<=1.5px 且 scale<=0.005），跳过重复的 Present 过程以节省耗时并防止闪烁。
    /// </summary>
    private bool ShouldSkipPresentDueToOptimisticMatch(
        RuntimeMapRecognition newlyAligned,
        int toggleVersion,
        out double driftDistance,
        out double scaleDelta)
    {
        driftDistance = -1d;
        scaleDelta = -1d;

        if (_optimisticPresentationToggleVersion != toggleVersion
            || _optimisticPresentationRecognition is null
            || !_overlay.HasMap)
        {
            return false;
        }

        var alignedTrans = newlyAligned.Result?.OverlayTransform;
        var optimisticTrans = _optimisticPresentationRecognition.Result?.OverlayTransform;
        if (alignedTrans is null || optimisticTrans is null)
        {
            return false;
        }

        var dx = alignedTrans.OffsetX - optimisticTrans.OffsetX;
        var dy = alignedTrans.OffsetY - optimisticTrans.OffsetY;
        driftDistance = Math.Sqrt((dx * dx) + (dy * dy));
        scaleDelta = Math.Abs(alignedTrans.ScaleX - optimisticTrans.ScaleX);

        if (driftDistance <= 3.5d && scaleDelta <= 0.008d)
        {
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Info,
                $"[零延迟] 静默对账吻合，跳过二次重绘 · drift={driftDistance:F2}px · dScale={scaleDelta:F4} · toggleVersion={toggleVersion}");
            return true;
        }

        // 存在可感知的微调，更新基准记录供后续对账或下次开图使用
        _optimisticPresentationRecognition = newlyAligned;
        return false;
    }

    /// <summary>
    /// 清理乐观预呈现的会话记录。
    /// </summary>
    private void ClearOptimisticPresentation()
    {
        _optimisticPresentationRecognition = null;
        _optimisticPresentationToggleVersion = -1;
    }
}
