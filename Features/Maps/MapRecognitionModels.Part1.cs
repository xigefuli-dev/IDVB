using OpenCvSharp;

namespace IDVBuff.Features.Maps;
public sealed partial class MapScanDiagnostics
{
    public int ScaleBootstrapUniqueMatches { get; set; }
    public int ScaleBootstrapPairVotes { get; set; }
    public double ScaleBootstrapResidualPixels { get; set; }
    public double ScaleBootstrapRelativeMad { get; set; }
    public string ScaleSeedSource { get; set; } = string.Empty;
    public string ScaleSeedCacheSource { get; set; } = string.Empty;
    public double ScaleSeedScale { get; set; }
    public bool ScaleSeedProjected { get; set; }
    public int ScaleSeedSourceViewportWidth { get; set; }
    public int ScaleSeedSourceViewportHeight { get; set; }
    public int ScaleSeedTargetViewportWidth { get; set; }
    public int ScaleSeedTargetViewportHeight { get; set; }
    public double ProjectedScale { get; set; }
    public double FinalValidatedScale { get; set; }
    public string ScaleSeedRejectionReason { get; set; } = string.Empty;
    public bool StructureEccConverged { get; set; }
    public double StructureEccCorrelation { get; set; }
    public double OverlayMilliseconds { get; set; }
    public double TotalMilliseconds { get; set; }

    // ── Initial/Steady alignment acceptance diagnostics ──
    public string AlignmentClass { get; set; } = string.Empty;
    public string AlignmentContextKey { get; set; } = string.Empty;
    public string AlignmentChannel { get; set; } = "standard";
    public string FloorMarkerKeys { get; set; } = string.Empty;
    public string AlignmentConfigFingerprint { get; set; } = string.Empty;
    private string _lowStructureRoute = string.Empty;
    public string LowStructureRoute
    {
        get => _lowStructureRoute;
        set
        {
            _lowStructureRoute = value;
            LowStructureEnteredCachedFixed = string.Equals(
                value,
                nameof(LowStructureAlignmentRoute.CachedFixed),
                StringComparison.Ordinal);
            if (!string.Equals(
                    value,
                    nameof(LowStructureAlignmentRoute.SparseCoarseSeed),
                    StringComparison.Ordinal)
                || LowStructureScaleSelectionContext.Current is not { } selection)
            {
                return;
            }
            LowStructureScaleResolutionRatio = selection.RelativeResolution;
            LowStructureScaleClusterTolerance =
                LowStructureScaleEvidenceRules.ResolveClusterTolerance(
                    selection.RelativeResolution);
            LowStructureScaleBasinCount = selection.BasinCount;
            LowStructureScaleSelectionAmbiguous = selection.Ambiguous;
            LowStructureScaleSelectionMilliseconds = selection.ElapsedMilliseconds;
        }
    }
    public string LowStructureReadinessDecision { get; set; } = string.Empty;
    public string LowStructureCacheTrustLevel { get; set; } = string.Empty;
    public int LowStructurePlannedScaleCount { get; set; }
    public int LowStructureCompletedScaleCount { get; set; }
    public int LowStructureRecoveryBatch { get; set; }
    public int LowStructureRecoveryTotalScaleCount { get; set; }
    public int LowStructureTranslationCandidateCount { get; set; }
    public string LowStructureBudgetTerminationReason { get; set; } = string.Empty;
    public bool LowStructureVpsgEnabled { get; set; }
    public int LowStructureEvidenceCount { get; set; }
    public int LowStructureEvidenceRequired { get; set; } = 1;
    public bool LowStructureEvidencePending { get; set; }
    public double LowStructureScaleResolutionRatio { get; set; }
    public double LowStructureScaleClusterTolerance { get; set; }
    public int LowStructureScaleBasinCount { get; set; }
    public bool LowStructureScaleSelectionAmbiguous { get; set; }
    public double LowStructureScaleSelectionMilliseconds { get; set; }
    public string LowStructureEvidenceRebuildReason { get; set; } = string.Empty;
    public bool LowStructureEnteredCachedFixed { get; set; }
    public double LowStructureScaleRelativeMad { get; set; }
    public bool WarmStateHit { get; set; }
    public string WarmStateMissReason { get; set; } = string.Empty;
    public double InputToFirstCaptureMilliseconds { get; set; }
    public double GameReadyDelayMilliseconds { get; set; }
    public double CoarseGlobalMilliseconds { get; set; }
    public double PyramidRefineMilliseconds { get; set; }
    public double ExactEvaluateMilliseconds { get; set; }
    public double FinalPresentMilliseconds { get; set; }
    public double InputToPresentMilliseconds { get; set; }
    public int PresentCount { get; set; }
    public int ReferenceDiskReadCount { get; set; }
    public int FullResolutionTemplateMatchCount { get; set; }
    public int StructurePreprocessCount { get; set; }
    public bool VpsgAttempted { get; set; }
    public bool VpsgActuallyEnabled { get; set; }
    public bool GateDetectionAttempted { get; set; }
    public bool UmatAttempted { get; set; }
    public int ScaleHypothesisCount { get; set; }

    // ── Phase 0: mutually-exclusive wall-clock timing ──

    /// <summary>Input → alignment start (animation wait + stability detection).</summary>
    public double InputToAlignmentStartMilliseconds { get; set; }
}
