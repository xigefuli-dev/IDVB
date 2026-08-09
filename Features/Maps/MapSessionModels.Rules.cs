using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public static class MapSessionRules
{
    // ── Confidence ──────────────────────────────────────────────
    private static double _highConfidence = 0.82d;
    private static double _mediumConfidence = 0.62d;
    private static double _minimumPlayerConfidence =
        PlayerTrackingRules.DefaultMinimumConfidence;
    private static int _mediumConfidenceConfirmationFrames = 3;
    private static int _backgroundFailureFrames = 3;

    public static double HighConfidence => _highConfidence;
    public static double MediumConfidence => _mediumConfidence;
    public static double MinimumPlayerConfidence => _minimumPlayerConfidence;
    public static int MediumConfidenceConfirmationFrames =>
        _mediumConfidenceConfirmationFrames;
    public static int BackgroundFailureFrames => _backgroundFailureFrames;

    // ── Stability ───────────────────────────────────────────────
    private static double _positionTolerancePixels = 3d;
    private static double _scaleToleranceRatio = 0.003d;
    private static double _rotationToleranceDegrees = 0.1d;
    private static int _maxHistoryEntries = 5;

    public static double PositionTolerancePixels => _positionTolerancePixels;
    public static double ScaleToleranceRatio => _scaleToleranceRatio;
    public static double RotationToleranceDegrees => _rotationToleranceDegrees;
    public static int MaxHistoryEntries => _maxHistoryEntries;

    // ── Evidence weights ────────────────────────────────────────
    private static double _weightAnchorGeometry = 0.20d;
    private static double _weightFeatureConsensus = 0.15d;
    private static double _weightCandidateSeparation = 0.10d;
    private static double _weightStructureQuality = 0.25d;
    private static double _weightRefinementQuality = 0.10d;
    private static double _weightBoundsAndPrior = 0.10d;
    private static double _weightTemporalStability = 0.10d;

    public static double WeightAnchorGeometry => _weightAnchorGeometry;
    public static double WeightFeatureConsensus => _weightFeatureConsensus;
    public static double WeightCandidateSeparation => _weightCandidateSeparation;
    public static double WeightStructureQuality => _weightStructureQuality;
    public static double WeightRefinementQuality => _weightRefinementQuality;
    public static double WeightBoundsAndPrior => _weightBoundsAndPrior;
    public static double WeightTemporalStability => _weightTemporalStability;

    // ── NativeScaleChangeRatio (no config model yet) ────────────
    public const double NativeScaleChangeRatio = 0.03d;

    // ── Configuration ───────────────────────────────────────────
    /// <summary>
    /// Initializes all configurable rules from the provided <see cref="IConfigProvider"/>.
    /// Call once during application startup. If never called, default values
    /// matching the original hardcoded constants are used.
    /// </summary>
    public static void Initialize(IConfigProvider config)
    {
        ArgumentNullException.ThrowIfNull(config);
        LoadFromConfig(config);
    }

    /// <summary>
    /// Hot-reloads all configurable rules from the provided <see cref="IConfigProvider"/>.
    /// Safe to call at any time; only replaces values for which the config
    /// returns a valid instance.
    /// </summary>
    public static void ReloadFromConfig(IConfigProvider config)
    {
        ArgumentNullException.ThrowIfNull(config);
        LoadFromConfig(config);
    }

    private static void LoadFromConfig(IConfigProvider config)
    {
        var confidence = config.Get<ConfidenceConfig>("confidence");
        if (confidence is not null)
        {
            _highConfidence = confidence.High;
            _mediumConfidence = confidence.Medium;
            _minimumPlayerConfidence = confidence.MinimumPlayerConfidence;
            _mediumConfidenceConfirmationFrames =
                confidence.MediumConfidenceFrames;
            _backgroundFailureFrames =
                confidence.BackgroundFailureFrames;
        }

        var stability = config.Get<StabilityConfig>("stability");
        if (stability is not null)
        {
            _positionTolerancePixels = stability.PositionTolerancePixels;
            _scaleToleranceRatio = stability.ScaleToleranceRatio;
            _rotationToleranceDegrees = stability.RotationToleranceDegrees;
            _maxHistoryEntries = stability.MaxHistoryEntries;
        }

        var evidenceWeights =
            config.Get<EvidenceWeightsConfig>("evidence_weights");
        if (evidenceWeights is not null)
        {
            _weightAnchorGeometry = evidenceWeights.AnchorGeometry;
            _weightFeatureConsensus = evidenceWeights.FeatureConsensus;
            _weightCandidateSeparation = evidenceWeights.CandidateSeparation;
            _weightStructureQuality = evidenceWeights.StructureQuality;
            _weightRefinementQuality = evidenceWeights.RefinementQuality;
            _weightBoundsAndPrior = evidenceWeights.BoundsAndPrior;
            _weightTemporalStability = evidenceWeights.TemporalStability;
        }
    }

    /// <summary>
    /// A locked transform is invalidated only after several consecutive,
    /// identity-matched contradictory tracking observations. Inconclusive
    /// searches and system failures are deliberately excluded by
    /// <see cref="MapAlignmentSession.HoldContinuousObservation"/>.
    /// </summary>
    public static bool ShouldLoseAlignmentLock(
        MapAlignmentSession? session,
        int requiredContradictoryFrames = 3) =>
        session is not null
        && session.ConsecutiveRejections
            >= Math.Max(1, requiredContradictoryFrames);

    /// <summary>
    /// Passive visual checks may validate or close an existing map session,
    /// but they are not authorized to create or retry one. Only an explicit
    /// game-map input or explicitly requested scan can start recognition.
    /// </summary>
    public static bool ShouldMonitorVisualPresence(MapSessionState state) =>
        state != MapSessionState.Closed;

    /// <summary>
    /// Passive validation is only meaningful for a successfully locked map.
    /// Failed or incomplete alignment states do not own a visible background,
    /// so polling their floor indicator wastes CPU and must not manufacture a
    /// close transition.
    /// </summary>
    public static bool ShouldRunPassiveSessionMonitor(
        MapSessionState state,
        bool scanInProgress) =>
        !scanInProgress && state == MapSessionState.Locked;

    public static bool CanContinueOpenPipeline(
        MapGameToggleState toggleState,
        MapGameToggleTransition transition,
        MapSessionState sessionState) =>
        sessionState != MapSessionState.Closed
        && toggleState.IsCurrent(transition);

    public static bool HasRequiredLockStability(
        double confidence,
        double highConfidence,
        bool skipStabilityConfirmation,
        int observedStableFrames,
        int requiredStableFrames) =>
        double.IsFinite(confidence)
        && (confidence >= highConfidence
            || skipStabilityConfirmation
            || observedStableFrames >= Math.Max(1, requiredStableFrames));

    public static bool IsValidTransition(
        MapSessionState current,
        MapSessionState next)
    {
        if (current == next)
            return true;
        if (next == MapSessionState.Closed)
            return true;
        return current switch
        {
            MapSessionState.Closed =>
                next == MapSessionState.OpeningDetected,
            MapSessionState.OpeningDetected =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.LowConfidence,
            MapSessionState.WaitingForStableFrames =>
                next is MapSessionState.IdentifyingMap
                    or MapSessionState.LowConfidence,
            MapSessionState.IdentifyingMap =>
                next is MapSessionState.CoarseLocating
                    or MapSessionState.LowConfidence,
            MapSessionState.CoarseLocating =>
                next is MapSessionState.FineLocating
                    or MapSessionState.LowConfidence,
            MapSessionState.FineLocating =>
                next is MapSessionState.Confirming
                    or MapSessionState.Locked
                    or MapSessionState.LowConfidence,
            MapSessionState.Confirming =>
                next is MapSessionState.Confirming
                    or MapSessionState.Locked
                    or MapSessionState.LowConfidence,
            MapSessionState.Locked =>
                next is MapSessionState.Lost
                    or MapSessionState.RecalibrationRequired,
            MapSessionState.LowConfidence =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.CoarseLocating
                    or MapSessionState.RecalibrationRequired,
            MapSessionState.Lost =>
                next is MapSessionState.RecalibrationRequired
                    or MapSessionState.WaitingForStableFrames,
            MapSessionState.RecalibrationRequired =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.CoarseLocating,
            _ => false
        };
    }

    public static MapViewportOrigin PredictViewportOrigin(
        MapReferencePoint player,
        double viewportScreenWidth,
        double viewportScreenHeight,
        double scale,
        MapReferenceBounds bounds)
    {
        if (!player.IsFinite
            || !double.IsFinite(scale)
            || scale <= 0d)
        {
            return new MapViewportOrigin(bounds.X, bounds.Y);
        }
        var width = viewportScreenWidth / scale;
        var height = viewportScreenHeight / scale;
        return bounds.ClampViewportOrigin(
            new MapViewportOrigin(
                player.X - (width / 2d),
                player.Y - (height / 2d)),
            width,
            height);
    }

    /// <summary>
    /// Reprojects a current screen-space player observation after a trusted
    /// alignment update. Its reference coordinate belongs to the transform
    /// and cannot be carried forward unchanged.
    /// </summary>
    public static MapPlayerState? ReprojectPlayer(
        MapPlayerState? player,
        MapSimilarityTransform transform,
        MapReferenceBounds bounds)
    {
        if (player is null || !transform.IsValid || !bounds.IsValid)
            return null;
        var reference = transform.ToReference(player.ScreenPoint);
        if (!reference.IsFinite || !bounds.Contains(reference, tolerance: 1d))
            return null;
        return new MapPlayerState
        {
            PlayerSlot = player.PlayerSlot,
            ViewportPoint = player.ViewportPoint,
            ScreenPoint = player.ScreenPoint,
            ReferencePoint = bounds.Clamp(reference),
            MarkerWidth = player.MarkerWidth,
            MarkerHeight = player.MarkerHeight,
            Confidence = player.Confidence,
            ObservedAt = player.ObservedAt
        };
    }

    public static MapRecalibrationReason GetSignatureChangeReason(
        MapWindowSignature locked,
        MapWindowSignature current)
    {
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(current);
        if (locked.ClientWidth != current.ClientWidth
            || locked.ClientHeight != current.ClientHeight)
        {
            return MapRecalibrationReason.ResolutionChanged;
        }
        if (locked.ViewportWidth != current.ViewportWidth
            || locked.ViewportHeight != current.ViewportHeight)
        {
            return MapRecalibrationReason.ViewportChanged;
        }
        // dwrg.exe and IDVB expose DPI-aware physical pixels. A DPI-only
        // monitor change affects presentation but not map geometry.
        if (locked.Dpi != current.Dpi)
            return MapRecalibrationReason.None;
        return MapRecalibrationReason.WindowChanged;
    }
}
