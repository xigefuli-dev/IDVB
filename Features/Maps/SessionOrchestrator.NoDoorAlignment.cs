using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private const int MaximumReliableFloorHistory = 8;
    private readonly object _reliableFloorAlignmentGate = new();
    private readonly Dictionary<MapAlignmentContextKey, WarmAlignmentState>
        _reliableFloorAlignments = [];

    private sealed record ReliableFloorAlignmentSeed(
        MapAlignmentSession Session,
        IReadOnlyList<MapSimilarityTransform> CandidateHistory,
        MapAlignmentContextKey ContextKey,
        double Confidence,
        double CandidateMargin);

    private void EnsureReliableFloorAlignmentScope(MapMatchSnapshot match)
    {
        lock (_reliableFloorAlignmentGate)
        {
            // Operation epochs change on open/close/floor operations. They are
            // not match identity changes and must not erase another floor's
            // reliable transform. A new MatchId naturally starts empty.
            if (!match.IsStarted)
                _reliableFloorAlignments.Clear();
        }
    }

    private MapAlignmentContextKey CreateAlignmentContextKey(
        MapMatchSnapshot match,
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey)
        => CreateAlignmentContextKey(
            match,
            frame.ClientBounds,
            frame.ViewportBounds,
            map,
            floorKey);

    private MapAlignmentContextKey CreateAlignmentContextKey(
        MapMatchSnapshot match,
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        MapRecord map,
        string floorKey)
    {
        var generationFingerprint =
            _settings?.StructureRegistrationTuning.Generation
                ?.CacheFingerprint ?? string.Empty;
        var channel = MapAlignmentChannelRegistry.Resolve(map, floorKey);
        var generation = generationFingerprint;
        if (channel.Channel == MapAlignmentChannel.LowStructure)
        {
            var effectiveTuning = CreateStructureTuningForFloor(
                map,
                floorKey,
                CreateEffectiveStructureTuning());
            generation = string.Join(
                "|",
                channel.DiagnosticLabel,
                effectiveTuning.CacheFingerprint,
                generationFingerprint);
        }
        return new MapAlignmentContextKey(
            match.MatchId,
            map.Id,
            map.UpdatedAt,
            floorKey,
            Math.Max(0, (int)Math.Round(clientBounds.Width)),
            Math.Max(0, (int)Math.Round(clientBounds.Height)),
            Math.Max(0, (int)Math.Round(viewportBounds.Width)),
            Math.Max(0, (int)Math.Round(viewportBounds.Height)),
            generation).Normalize();
    }

    private static string? GetWarmStateMissReason(
        MapAlignmentContextKey requested,
        WarmAlignmentState? state,
        MapRecord map,
        string floorKey)
    {
        if (state is null)
            return "missing-state";
        if (state.ContextKey.MapId != requested.MapId
            || state.ContextKey.MapUpdatedAt != requested.MapUpdatedAt)
            return "map-mismatch";
        if (!string.Equals(state.ContextKey.FloorKey, requested.FloorKey,
                StringComparison.Ordinal))
            return "floor-mismatch";
        if (state.ContextKey.ClientWidth != requested.ClientWidth
            || state.ContextKey.ClientHeight != requested.ClientHeight
            || state.ContextKey.ViewportWidth != requested.ViewportWidth
            || state.ContextKey.ViewportHeight != requested.ViewportHeight)
            return "resolution-mismatch";
        if (!string.Equals(state.ContextKey.StructureGeneration,
                requested.StructureGeneration, StringComparison.Ordinal))
            return "generation-mismatch";
        if (!MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
                state.Session,
                map.Id,
                map.UpdatedAt,
                floorKey,
                0d))
            return "transform-invalid";
        return "confidence-insufficient";
    }

    private ReliableFloorAlignmentSeed? TryGetReliableFloorAlignment(
        MapMatchSnapshot match,
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out string missReason)
        => TryGetReliableFloorAlignment(
            match,
            frame.ClientBounds,
            frame.ViewportBounds,
            map,
            floorKey,
            out missReason);

    private ReliableFloorAlignmentSeed? TryGetReliableFloorAlignment(
        MapMatchSnapshot match,
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        MapRecord map,
        string floorKey,
        out string missReason)
    {
        EnsureReliableFloorAlignmentScope(match);
        var key = CreateAlignmentContextKey(
            match,
            clientBounds,
            viewportBounds,
            map,
            floorKey);
        missReason = string.Empty;
        lock (_reliableFloorAlignmentGate)
        {
            _reliableFloorAlignments.TryGetValue(key, out var state);
            var channel = MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel;
            if (state is null
                || !MapOpenAlignmentRouteRules.CanUseWarmAlignmentState(
                    channel,
                    state.IsScaleReliable)
                || !MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
                    state.Session,
                    map.Id,
                    map.UpdatedAt,
                    floorKey,
                    _settings!.SessionTuning.HighConfidence)
                || !state.LastTransform.IsValid)
            {
                var nearestState = state ?? _reliableFloorAlignments.Values
                    .Where(candidate => candidate.ContextKey.MatchId == key.MatchId)
                    .OrderByDescending(candidate => candidate.LastValidatedAt)
                    .FirstOrDefault();
                missReason = GetWarmStateMissReason(key, nearestState, map, floorKey)
                    ?? "confidence-insufficient";
                if (state is not null
                    && !MapOpenAlignmentRouteRules.CanUseWarmAlignmentState(
                        channel,
                        state.IsScaleReliable))
                {
                    missReason = "same-floor-scale-provisional";
                }
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"Steady 热状态未命中 · reason={missReason}",
                    details: new()
                    {
                        ["warmStateMissReason"] = missReason,
                        ["alignmentContextKey"] = key.ToString(),
                        ["scaleReliable"] = state?.IsScaleReliable,
                        ["adaptiveScaleNoLongerGatesWarmState"] =
                            channel != MapAlignmentChannel.LowStructure
                    });
                return null;
            }

            return new ReliableFloorAlignmentSeed(
                state.Session,
                state.RecentTransforms.ToArray(),
                state.ContextKey,
                state.Confidence,
                state.CandidateMargin);
        }
    }

    private void RememberReliableFloorAlignment(
        MapMatchSnapshot match,
        RuntimeMapRecognition recognition,
        MapAlignmentSession? session,
        CapturedGameFrame frame,
        bool isScaleReliable)
    {
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
            || !MapSimilarityTransform.FromOverlay(transform).IsValid)
        {
            return;
        }

        var reliableSession = session!;
        EnsureReliableFloorAlignmentScope(match);
        var key = CreateAlignmentContextKey(
            match,
            frame,
            recognition.Map,
            recognition.Result.Floor);
        var similarity = MapSimilarityTransform.FromOverlay(transform);
        int successCount;
        bool effectiveScaleReliable;
        lock (_reliableFloorAlignmentGate)
        {
            if (!_reliableFloorAlignments.TryGetValue(key, out var state))
            {
                state = new WarmAlignmentState
                {
                    ContextKey = key,
                    Session = reliableSession,
                    LastTransform = similarity,
                    Confidence = recognition.Result.LocalizationConfidence,
                    CandidateMargin = MapFeatureCacheRules.GetCandidateMargin(
                        recognition.Result),
                    IsScaleReliable = isScaleReliable,
                    LastValidatedAt = DateTimeOffset.UtcNow,
                    SuccessCount = 1
                };
                _reliableFloorAlignments[key] = state;
            }
            else
            {
                state.Session = reliableSession;
                state.LastTransform = similarity;
                state.Confidence = recognition.Result.LocalizationConfidence;
                state.CandidateMargin = MapFeatureCacheRules.GetCandidateMargin(
                    recognition.Result);
                // A later provisional observation must not undo a player lock
                // or a previously confirmed same-floor scale.
                state.IsScaleReliable |= isScaleReliable;
                state.LastValidatedAt = DateTimeOffset.UtcNow;
                state.SuccessCount++;
            }

            RememberAdaptiveReliableKey(
                recognition,
                string.Equals(
                    recognition.Result.Floor,
                    MapFloorRules.GetPrimaryFloorKey(recognition.Map),
                    StringComparison.Ordinal));

            var duplicate = state.RecentTransforms.Any(candidate =>
                Math.Abs(candidate.Scale - similarity.Scale) <= 0.0005d
                && Math.Abs(candidate.TranslationX - similarity.TranslationX) <= 1d
                && Math.Abs(candidate.TranslationY - similarity.TranslationY) <= 1d);
            if (!duplicate)
                state.RecentTransforms.Add(similarity);
            while (state.RecentTransforms.Count > MaximumReliableFloorHistory)
                state.RecentTransforms.RemoveAt(0);
            successCount = state.SuccessCount;
            effectiveScaleReliable = state.IsScaleReliable;
        }

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"Steady 热状态已写入 · context={key}",
            details: new()
            {
                ["warmStateHit"] = true,
                ["successCount"] = successCount,
                ["scaleReliable"] = effectiveScaleReliable,
                ["adaptiveScaleGatesWarmState"] = true
            });
    }

    private void ForgetReliableFloorAlignment(MapAlignmentContextKey key)
    {
        lock (_reliableFloorAlignmentGate)
            _reliableFloorAlignments.Remove(key.Normalize());
    }

    private void MarkReliableFloorScale(
        RuntimeMapRecognition recognition,
        double scale)
    {
        var match = _matchSession.Snapshot;
        if (!match.IsStarted || !double.IsFinite(scale) || scale <= 0d)
            return;

        lock (_reliableFloorAlignmentGate)
        {
            foreach (var state in _reliableFloorAlignments.Values)
            {
                if (state.ContextKey.MatchId != match.MatchId
                    || state.ContextKey.MapId != recognition.Map.Id
                    || state.ContextKey.MapUpdatedAt != recognition.Map.UpdatedAt
                    || !string.Equals(
                        state.ContextKey.FloorKey,
                        recognition.Result.Floor,
                        StringComparison.Ordinal)
                    || Math.Abs(state.Session.LockedTransform.ScaleX - scale)
                        > 0.0005d)
                {
                    continue;
                }

                state.IsScaleReliable = true;
            }
        }
    }

    private static bool TryCreateNoDoorStageTuning(
        MapStructureRegistrationTuning source,
        out MapStructureRegistrationTuning tuning,
        int? maximumStageMilliseconds = null)
    {
        tuning = source.Clone();
        if (tuning.Channel == MapAlignmentChannel.LowStructure)
        {
            // This helper belongs to the standard no-door route. Keep the
            // low-structure fallback safe if a future caller reaches it:
            // low floors remain structure-only and budgeted.
            tuning.EnforceTimeBudget = true;
            tuning.StructureFallbackBudgetMilliseconds = Math.Clamp(
                tuning.LowStructureColdPathBudgetMilliseconds,
                50,
                700);
            tuning.Normalize();
            return true;
        }
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

    // 辅助锚点已停用（TryAlignNoDoorWithAuxiliaryAnchors 已移除）。
}
/*
 * 文件职责：SessionOrchestrator.NoDoorAlignment。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
