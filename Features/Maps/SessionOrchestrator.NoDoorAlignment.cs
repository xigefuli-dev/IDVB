using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private const int MaximumReliableFloorHistory = 4;
    private readonly object _reliableFloorAlignmentGate = new();
    private readonly Dictionary<ReliableFloorAlignmentKey, ReliableFloorAlignmentState>
        _reliableFloorAlignments = [];
    private int _reliableFloorAlignmentMatchVersion = -1;

    private readonly record struct ReliableFloorAlignmentKey(
        Guid MapId,
        DateTimeOffset MapUpdatedAt,
        string FloorKey);

    private sealed class ReliableFloorAlignmentState
    {
        public required MapAlignmentSession Session { get; set; }
        public AdaptiveScaleKey? AdaptiveKey { get; set; }
        public List<MapSimilarityTransform> CandidateHistory { get; } = [];
    }

    private sealed record ReliableFloorAlignmentSeed(
        MapAlignmentSession Session,
        IReadOnlyList<MapSimilarityTransform> CandidateHistory);

    /// <summary>
    /// One deadline shared by stable-frame capture and every synchronous
    /// no-door fallback. Synchronous stages retain their existing signatures;
    /// they consult <see cref="Current"/> and receive only the time remaining.
    /// </summary>
    private sealed class NoDoorAlignmentDeadline : IDisposable
    {
        private static readonly AsyncLocal<NoDoorAlignmentDeadline?> Ambient = new();
        private readonly CancellationToken _parentToken;
        private readonly CancellationTokenSource _linkedCancellation;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public NoDoorAlignmentDeadline(
            CancellationToken parentToken,
            int budgetMilliseconds)
        {
            _parentToken = parentToken;
            BudgetMilliseconds =
                MapOpenAlignmentRouteRules.ResolveNoDoorAlignmentBudgetMilliseconds(
                    budgetMilliseconds);
            _linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _linkedCancellation.CancelAfter(BudgetMilliseconds);
        }

        public static NoDoorAlignmentDeadline? Current => Ambient.Value;
        public int BudgetMilliseconds { get; }
        public CancellationToken Token => _linkedCancellation.Token;
        public double ElapsedMilliseconds => _stopwatch.Elapsed.TotalMilliseconds;
        public int RemainingMilliseconds => Math.Max(
            0,
            BudgetMilliseconds - (int)Math.Ceiling(ElapsedMilliseconds));
        public bool IsExpired =>
            _linkedCancellation.IsCancellationRequested
            || RemainingMilliseconds <= 0;
        public bool TimedOut =>
            !_parentToken.IsCancellationRequested
            && IsExpired;

        public bool CanStartStage(
            int minimumMilliseconds =
                MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds) =>
            !IsExpired && RemainingMilliseconds >= minimumMilliseconds;

        public IDisposable EnterAmbient()
        {
            var previous = Ambient.Value;
            Ambient.Value = this;
            return new AmbientLease(
                previous,
                MapNoDoorAlignmentBudgetContext.Enter(
                    () => RemainingMilliseconds));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopwatch.Stop();
            _linkedCancellation.Dispose();
        }

        private sealed class AmbientLease(
            NoDoorAlignmentDeadline? previous,
            IDisposable budgetLease) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                budgetLease.Dispose();
                Ambient.Value = previous;
            }
        }
    }

    private void EnsureReliableFloorAlignmentScope(MapMatchSnapshot match)
    {
        lock (_reliableFloorAlignmentGate)
        {
            if (_reliableFloorAlignmentMatchVersion == match.Version)
                return;
            _reliableFloorAlignments.Clear();
            _reliableFloorAlignmentMatchVersion = match.Version;
        }
    }

    private ReliableFloorAlignmentSeed? TryGetReliableFloorAlignment(
        MapMatchSnapshot match,
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey)
    {
        EnsureReliableFloorAlignmentScope(match);
        var key = new ReliableFloorAlignmentKey(
            map.Id,
            map.UpdatedAt,
            floorKey);
        var adaptiveKey = CreateAdaptiveScaleKey(frame, map, floorKey);
        lock (_reliableFloorAlignmentGate)
        {
            if (!_reliableFloorAlignments.TryGetValue(key, out var state)
                || !MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
                    state.Session,
                    map.Id,
                    map.UpdatedAt,
                    floorKey,
                    _settings!.SessionTuning.HighConfidence)
                || (IsAdaptiveScaleEnabled && state.AdaptiveKey != adaptiveKey)
                || !CanUseAdaptiveReliableSession(state.Session, adaptiveKey))
            {
                return null;
            }

            return new ReliableFloorAlignmentSeed(
                state.Session,
                state.CandidateHistory.ToArray());
        }
    }

    private void RememberReliableFloorAlignment(
        MapMatchSnapshot match,
        RuntimeMapRecognition recognition,
        MapAlignmentSession? session)
    {
        var hasAdaptiveKey = TryGetActiveAdaptiveKey(recognition, out var adaptiveKey);
        if (!match.IsStarted
            || !_matchSession.IsCurrent(match)
            || session is null
            || recognition.Result.ReusedLastTransform
            || recognition.Result.OverlayTransform is not { } transform
            || !MapFeatureCacheRules.IsReliableLocalizationSample(
                recognition.Result,
                _settings!.SessionTuning.HighConfidence,
                _settings.StructureRegistrationTuning.MinimumCandidateMargin)
            || !MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
                session,
                recognition.Map.Id,
                recognition.Map.UpdatedAt,
                recognition.Result.Floor,
                _settings.SessionTuning.HighConfidence)
            || (IsAdaptiveScaleEnabled && !hasAdaptiveKey)
            || (hasAdaptiveKey
                && !CanUseAdaptiveReliableSession(session, adaptiveKey)))
        {
            return;
        }

        EnsureReliableFloorAlignmentScope(match);
        var key = new ReliableFloorAlignmentKey(
            recognition.Map.Id,
            recognition.Map.UpdatedAt,
            recognition.Result.Floor);
        var similarity = MapSimilarityTransform.FromOverlay(transform);
        lock (_reliableFloorAlignmentGate)
        {
            if (!_reliableFloorAlignments.TryGetValue(key, out var state))
            {
                state = new ReliableFloorAlignmentState
                {
                    Session = session,
                    AdaptiveKey = hasAdaptiveKey ? adaptiveKey : null
                };
                _reliableFloorAlignments[key] = state;
            }
            else
            {
                state.Session = session;
                state.AdaptiveKey = hasAdaptiveKey ? adaptiveKey : null;
            }

            RememberAdaptiveReliableKey(
                recognition,
                string.Equals(
                    recognition.Result.Floor,
                    MapFloorRules.GetPrimaryFloorKey(recognition.Map),
                    StringComparison.Ordinal));

            var duplicate = state.CandidateHistory.Any(candidate =>
                Math.Abs(candidate.Scale - similarity.Scale) <= 0.0005d
                && Math.Abs(candidate.TranslationX - similarity.TranslationX) <= 1d
                && Math.Abs(candidate.TranslationY - similarity.TranslationY) <= 1d);
            if (!duplicate)
                state.CandidateHistory.Add(similarity);
            while (state.CandidateHistory.Count > MaximumReliableFloorHistory)
                state.CandidateHistory.RemoveAt(0);
        }
    }

    private static bool TryCreateNoDoorStageTuning(
        MapStructureRegistrationTuning source,
        out MapStructureRegistrationTuning tuning,
        int? maximumStageMilliseconds = null)
    {
        tuning = source.Clone();
        var deadline = NoDoorAlignmentDeadline.Current;
        if (deadline is null)
        {
            tuning.Normalize();
            return true;
        }
        if (!deadline.CanStartStage())
            return false;

        tuning.StructureFallbackBudgetMilliseconds = Math.Min(
            deadline.RemainingMilliseconds,
            maximumStageMilliseconds is { } maximum
                ? Math.Max(
                    MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds,
                    maximum)
                : deadline.RemainingMilliseconds);
        tuning.Normalize();
        return true;
    }

    private MapRecognitionAttempt CreateNoDoorBudgetFailure(
        string stage,
        MapScanDiagnostics? diagnostics = null)
    {
        var deadline = NoDoorAlignmentDeadline.Current;
        diagnostics ??= MapCvRecognitionDiagnostics.CreateDiagnostics(
            _recognition.ReadyMapCount,
            _recognition.TotalMapCount);
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = false;
        diagnostics.StructureRejectionReason =
            MapStructureRejectionReason.TimeBudgetExceeded;
        diagnostics.StructureDisposition =
            MapStructureEvidenceDisposition.Inconclusive;
        diagnostics.TotalMilliseconds = Math.Max(
            diagnostics.TotalMilliseconds,
            deadline?.ElapsedMilliseconds ?? 0d);
        var reason = $"无门对齐总预算已耗尽（阶段：{stage}），已停止后续恢复；请保持地图打开并重试。";
        var structure = MapStructureRegistrationResult.Reject(
            MapStructureRejectionReason.TimeBudgetExceeded,
            reason);
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Warning,
            reason,
            elapsedMs: deadline?.ElapsedMilliseconds,
            details: new()
            {
                ["stage"] = stage,
                ["budgetMs"] = deadline?.BudgetMilliseconds,
                ["remainingMs"] = deadline?.RemainingMilliseconds ?? 0,
                ["cancelled"] = deadline?.Token.IsCancellationRequested ?? false
            });
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            FailureReason = reason,
            StructureAttempted = true,
            StructureAccepted = false,
            StructureFailureReason = reason,
            SearchStage = AlignmentSearchStage.StructureFallback
        };
    }

    private void LogNoDoorStage(
        string stage,
        bool succeeded,
        MapRecognitionAttempt? attempt = null,
        double? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? additionalDetails = null)
    {
        var deadline = NoDoorAlignmentDeadline.Current;
        var details = new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["succeeded"] = succeeded,
            ["budgetMs"] = deadline?.BudgetMilliseconds,
            ["remainingMs"] = deadline?.RemainingMilliseconds,
            ["rejection"] = attempt?.StructureResult?.RejectionReason.ToString(),
            ["failureReason"] = attempt?.FailureReason,
            ["identityConfidence"] =
                attempt?.Recognition?.Result.IdentityConfidence,
            ["localizationConfidence"] =
                attempt?.Recognition?.Result.LocalizationConfidence,
            ["candidateMargin"] = attempt?.Recognition is { } recognition
                ? MapFeatureCacheRules.GetCandidateMargin(recognition.Result)
                : attempt?.StructureResult?.CandidateMargin,
            ["offsetX"] = attempt?.Recognition?.Result.OverlayTransform?.OffsetX,
            ["offsetY"] = attempt?.Recognition?.Result.OverlayTransform?.OffsetY,
            ["targetP50Ms"] =
                MapOpenAlignmentRouteRules.TargetP50Milliseconds,
            ["targetP95Ms"] =
                MapOpenAlignmentRouteRules.TargetP95Milliseconds,
            ["maximumFailureMs"] =
                MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds,
            ["targetReliableRate"] =
                MapOpenAlignmentRouteRules.TargetReliableAlignmentRate,
            ["targetJitterP95Px"] =
                MapOpenAlignmentRouteRules.TargetTranslationJitterP95Pixels
        };
        if (additionalDetails is not null)
        {
            foreach (var pair in additionalDetails)
                details[pair.Key] = pair.Value;
        }
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            succeeded ? MapLogLevel.Info : MapLogLevel.Warning,
            $"无门对齐阶段完成 · {stage} · success={succeeded}",
            elapsedMs: elapsedMilliseconds,
            details: details);
    }

    private MapRecognitionAttempt AlignNoDoorLocalStructure(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapAlignmentSession sameFloorSession,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        IReadOnlyList<MapSimilarityTransform> candidateHistory,
        double identityPriorConfidence,
        bool allowTrackingScaleSearch = false)
    {
        if (!TryCreateNoDoorStageTuning(
                structureTuning,
                out var localTuning,
                maximumStageMilliseconds: 500))
        {
            return CreateNoDoorBudgetFailure("same-floor-local");
        }

        var totalTimer = Stopwatch.StartNew();
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            _recognition.ReadyMapCount,
            _recognition.TotalMapCount);
        if (alignmentMode != MapOverlayAlignmentMode.Uniform)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "无门楼层局部跟踪只支持等比缩放。");
        }

        var profile = MapFloorRules.GetFloorProfile(locked.Map, floorKey);
        if (profile is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"地图中不存在楼层 '{floorKey}'。");
        }

        var referencePath = _recognition.Repository.GetFloorRecognitionPath(
            locked.Map,
            floorKey);
        var referenceTimer = Stopwatch.StartNew();
        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        referenceTimer.Stop();
        diagnostics.ReferenceImageLoadMilliseconds =
            referenceTimer.Elapsed.TotalMilliseconds;
        if (reference.Empty())
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"无法读取楼层 '{floorKey}' 的识别图。");
        }

        var cacheTimer = Stopwatch.StartNew();
        using var preparedReference = _recognition.StructureCache.GetOrCreate(
            locked.Map.Id,
            locked.Map.UpdatedAt,
            reference,
            profile.WholeImageIgnoreRegions,
            floorKey);
        cacheTimer.Stop();
        diagnostics.CacheMilliseconds = cacheTimer.Elapsed.TotalMilliseconds;
        diagnostics.ReferenceCacheMilliseconds =
            cacheTimer.Elapsed.TotalMilliseconds;
        var preparedLive = frame.GetOrCreateDefaultLiveStructureFeatures(
            _recognition.StructurePreprocessor,
            MapStructurePreprocessingProfile.EdgesOnly,
            out var liveCacheHit,
            out var liveExtractionMilliseconds,
            out _);
        diagnostics.StructurePreprocessMilliseconds = liveCacheHit
            ? 0d
            : liveExtractionMilliseconds;
        diagnostics.LiveStructurePreprocessMilliseconds =
            diagnostics.StructurePreprocessMilliseconds;
        if (NoDoorAlignmentDeadline.Current?.IsExpired == true)
            return CreateNoDoorBudgetFailure("same-floor-local-preprocess", diagnostics);

        if (!TryCreateNoDoorStageTuning(
                localTuning,
                out var postPreprocessTuning,
                maximumStageMilliseconds: 500))
        {
            return CreateNoDoorBudgetFailure(
                "same-floor-local-preprocess",
                diagnostics);
        }
        localTuning = postPreprocessTuning;

        localTuning.ScaleSearchRadius = 0d;
        if (!allowTrackingScaleSearch)
            localTuning.TrackingScaleSearchRadius = 0d;
        localTuning.TrackingSearchRadiusPixels =
            localTuning.PreviousAlignmentSearchRadiusPixels;
        localTuning.EnableFeatureVoting = false;
        var structure = _recognition.StructureRegistrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = sameFloorSession.LockedTransform,
                Tuning = localTuning,
                ScaleSearchPolicy = allowTrackingScaleSearch
                    ? MapScaleSearchPolicy.Search
                    : MapScaleSearchPolicy.Fixed,
                RestrictSearchToLockedTransform = true,
                TrackingMode = true,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = profile.OrientationDegrees,
                ValidMapBounds = profile.GetEffectiveValidMapBounds(),
                CandidateHistory = candidateHistory,
                SideEntrancePrior = 0d
            });
        totalTimer.Stop();
        MapCvAlignmentService.PopulateStructureDiagnostics(
            diagnostics,
            structure);
        diagnostics.StructureSearchMilliseconds = structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds = structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
        diagnostics.TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;
        if (NoDoorAlignmentDeadline.Current?.IsExpired == true)
            return CreateNoDoorBudgetFailure("same-floor-local", diagnostics);

        if (!structure.Accepted
            || structure.Transform is null
            || structure.Confidence < tuning.MinimumConfidence)
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason = structure.FailureReason,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = structure.FailureReason,
                SearchStage = AlignmentSearchStage.StructureFallback
            };
        }

        diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition =
                MapCvRecognitionBuilders.BuildFloorStructureRecognition(
                    locked.Map,
                    floorKey,
                    _recognition.Repository.GetFloorOverlayPath(
                        locked.Map,
                        floorKey),
                    structure.Transform,
                    structure,
                    identityPriorConfidence),
            StructureAttempted = true,
            StructureAccepted = true,
            SearchStage = AlignmentSearchStage.StructureFallback
        };
    }

    // 辅助锚点已停用（TryAlignNoDoorWithAuxiliaryAnchors 已移除）。
}
