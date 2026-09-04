using OpenCvSharp;

namespace IDVBuff.Features.Maps;
public sealed partial class MapScanDiagnostics
{
    public string ScaleBootstrapMode { get; set; } = string.Empty;
    public double? ScaleBootstrapLegacyScale { get; set; }
    public double ScaleBootstrapLegacyConfidence { get; set; }
    public double ScaleBootstrapLegacyMilliseconds { get; set; }
    public double ScaleBootstrapStructureMilliseconds { get; set; }
    public int ScaleBootstrapCandidateCount { get; set; }
    public int ScaleBootstrapSelectedCandidateIndex { get; set; }

    public double InputToLockedMilliseconds { get; set; }
    public double VisibleMaskMs { get; set; }
    public double VisibleFraction { get; set; }
    public int VisibleStructurePixels { get; set; }
    public int VisibleEdgePixels { get; set; }
    public double VisibleAwareSearchMs { get; set; }
    public int VisibleAwareCandidateCount { get; set; }
    public double VisibleAwareTopCost { get; set; }
    public double VisibleAwareTopMargin { get; set; }
    public bool VisibleAwareEarlyAccepted { get; set; }
    public string? VisibleAwareFallbackReason { get; set; }
    public string VisibleAwareRequestedBackend { get; set; } = string.Empty;
    public string VisibleAwareActualBackend { get; set; } = string.Empty;
    public string? VisibleAwareUMatFallbackReason { get; set; }
    public double VisibleAwareCoarseMs { get; set; }
    public double VisibleAwareRefineMs { get; set; }
    public double VisibleAwareUploadMs { get; set; }
    public double VisibleAwareDownloadMs { get; set; }
    public int VisibleAwareCompletedScaleCount { get; set; }
    public int VisibleAwareBudgetSkippedScaleCount { get; set; }
    public int VisibleAwareCoarsePeakCount { get; set; }
    public int VisibleAwareRefinedCandidateCount { get; set; }

    // Fast alignment diagnostics
    public bool StructureFastStrategyUsed { get; set; }
    public double StructureCoarseSearchMs { get; set; }
    public int StructureCoarseCandidateCount { get; set; }

    // Scan verification diagnostics
    public int ScanCandidateCount { get; set; }
    public int ScanVerificationCandidateCount { get; set; }
    public int ScanCheapRejectCount { get; set; }
    public double ScanCheapRejectMilliseconds { get; set; }
    public int ScanFormalStructureAttemptCount { get; set; }
    public int ScanFormalStructureCompletedCount { get; set; }
    public int ScanFormalStructureAcceptedCount { get; set; }
    public int ScanShadowPairCount { get; set; }
    public int ScanShadowTrueFormalFalseCount { get; set; }
    public int ScanShadowFalseFormalTrueCount { get; set; }
    public int ScanShadowTrueFormalTrueCount { get; set; }
    public int ScanShadowFalseFormalFalseCount { get; set; }
    public bool ScanShadowCollectionEnabled { get; set; }
    public int ScanEffectiveBudgetMilliseconds { get; set; }
    public int ScanVpsgAttemptCount { get; set; }
    public int ScanFullRecoveryCount { get; set; }
    public double ScanTotalVerificationMilliseconds { get; set; }
    public double ScanCandidate0TemplateValidationMilliseconds { get; set; }
    public double ScanCandidate0VpsgMilliseconds { get; set; }
    public double ScanCandidate0StructureMilliseconds { get; set; }
    public bool ScanCheapRejected { get; set; }
    public bool ScanVpsgAttempted { get; set; }
    public bool ScanFullRecoveryAttempted { get; set; }
    public double ScanTemplateValidationMilliseconds { get; set; }
    public double ScanVpsgMilliseconds { get; set; }
    public double ScanStructureMilliseconds { get; set; }

    public string ToStatusText() =>
        $"地图 {_ReadyText()}"
        + (DetectedFloor is { } floor
            ? $" · 楼层 {floor.ToUpperInvariant()} {FloorRequestMilliseconds:F1}ms"
                + (FloorRequestMilliseconds
                        > MapFloorRecognitionRules.PerformanceBudgetMilliseconds
                    ? "（超过100ms目标）"
                    : string.Empty)
            : string.Empty)
        + $" · 捕获 {CaptureMilliseconds:F0}ms · 门 {GateDetectionMilliseconds:F0}ms · 排名 {GeometryMilliseconds:F0}ms"
        + (AuxiliaryAnchorMilliseconds > 0d
            ? $" · 辅助锚点 {AuxiliaryAnchorMilliseconds:F0}ms/{AuxiliaryAnchorMatchCount}"
            : string.Empty)
        + (ConfirmationMilliseconds > 0 ? $" · 复核 {ConfirmationMilliseconds:F0}ms" : string.Empty)
        + (UsedSingleGateStructureFallback ? " · 单门复核失败，已回退结构" : string.Empty)
        + (SideEntranceEligibleMapCount > 0
            ? $" · 侧门就绪 {SideEntranceReadyMapCount}/{SideEntranceEligibleMapCount}"
                + $" · 拒绝 {SideEntranceRejectedCandidateCount}"
            : string.Empty)
        + (UsedForcedBestResult ? " · 已强制采用最优结果" : string.Empty)
        + (StructureSearchMilliseconds > 0
            ? $" · 结构 {StructurePreprocessMilliseconds + StructureSearchMilliseconds + StructureRefineMilliseconds:F0}ms"
            : string.Empty)
        + (SkippedStructureValidation ? " · 已跳过结构复核" : string.Empty)
        + (StructureRejectionReason != MapStructureRejectionReason.None
            ? $" · 拒绝 {StructureRejectionReason.ToDisplayText()}"
            : string.Empty)
        + $" · 总计 {TotalMilliseconds:F0}ms";

    private string _ReadyText() => $"{ReadyMapCount}/{TotalMapCount} 就绪";
}
