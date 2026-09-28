using IDVBuff.Features.Maps;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private static string FormatSessionSnapshot(MapSessionSnapshot snapshot)
    {
        var summary =
            $"{snapshot.State} · {snapshot.LocationMethod}"
            + $" · 会话版本 {snapshot.Version}"
            + $" · 对齐修订 {snapshot.AlignmentRevision}"
            + (snapshot.MapId is { } mapId
                ? $" · 地图 {mapId.ToString("N")[..8]}"
                : string.Empty)
            + (snapshot.Floor is { } floor
                ? $" · {floor.ToUpperInvariant()}"
                : string.Empty)
            + (snapshot.State == MapSessionState.LowConfidence
                    || snapshot.Confidence > 0d
                ? $" · 置信度 {snapshot.Confidence:P1}"
                : string.Empty)
            + (snapshot.StableCandidateFrames > 0
                ? $" · 稳定帧 {snapshot.StableCandidateFrames}"
                : string.Empty);
        if (snapshot.ViewportOrigin is { } origin)
            summary += $" · 视口原点 ({origin.X:F1}, {origin.Y:F1})";
        if (snapshot.LockedTransform is { } transform)
        {
            summary +=
                $" · S={transform.Scale:F4}"
                + $" R={transform.RotationDegrees:F1}°"
                + $" T=({transform.TranslationX:F1}, {transform.TranslationY:F1})";
        }
        if (snapshot.RecalibrationReason != MapRecalibrationReason.None)
            summary += $" · 失效原因 {snapshot.RecalibrationReason}";
        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
            summary += $" · {snapshot.Detail}";
        return summary;
    }

    private static string FormatRecognition(RuntimeMapRecognition recognition)
    {
        var source = recognition.Result.Source switch
        {
            MapRecognitionSource.ManualGateSelection => "手动门点",
            MapRecognitionSource.UserConfirmed => "手动确认",
            MapRecognitionSource.SelectedMapGatePair => "双门完整对齐",
            MapRecognitionSource.SingleGateTracking => "单门跟踪（缩放锁定）",
            MapRecognitionSource.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
            MapRecognitionSource.StructureMatching => "局部地图结构配准",
            MapRecognitionSource.ReusedLastTransform => "复用上次可靠对齐",
            _ => "自动识别"
        };
        var summary =
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()} · {recognition.Result.Confidence:P0} · {source}"
            + $" · 证据 {recognition.Result.EvidenceKind}"
            + (recognition.Result.SkippedStructureValidation
                ? " · 已跳过结构复核"
                : $" · 结构 {recognition.Result.StructureDisposition}")
            + (recognition.Result.WasForcedBestResult
                ? " · 强制呈现"
                : string.Empty);
        if (recognition.Result.OverlayTransform is not { } transform)
            return summary;
        return summary
            + $" · {transform.AlignmentMode.ToDisplayName()}"
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 平均边缘距离 {transform.MaximumResidualPixels:F1}px"
                : $" · 最大误差 {transform.MaximumResidualPixels:F1}px")
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 候选差距 {recognition.Result.StructureCandidateMargin:P1}"
                : string.Empty)
            + (transform.UsedDegenerateAxisFallback ? " · 退化轴回退" : string.Empty)
            + (transform.IsExactFit ? " · 已贴合" : " · 未完全贴合");
    }

    private static string FormatAlignmentState(
        MapAlignmentTrackingMode mode) => mode switch
    {
        MapAlignmentTrackingMode.NeedsGatePair => "需要双门完成本次运行的缩放锁定",
        MapAlignmentTrackingMode.GatePairLocked => "双门完整对齐",
        MapAlignmentTrackingMode.SingleGateTracking => "单门跟踪（缩放锁定）",
        MapAlignmentTrackingMode.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
        MapAlignmentTrackingMode.WaitingForAnchor => "等待可信门点或结构证据恢复",
        MapAlignmentTrackingMode.StructureMatched => "局部地图结构配准",
        MapAlignmentTrackingMode.HoldingLastTransform => "结构证据不足，已保留最后可靠对齐",
        MapAlignmentTrackingMode.Lost => "对齐已失效，需要双门重新锁定",
        _ => "尚无运行时对齐"
    };
}
