namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly record struct CandidateSelectionResolution(
        RuntimeMapRecognition? Recognition,
        bool StartSurvey,
        RuntimeMapRecognition? VerifiedAlignment = null);

    private IMapCandidateSelector? _activeCandidateSelector;
    private IReadOnlyList<MapRecognitionChoice> _lastCandidateChoices = [];

    public IReadOnlyList<MapRecognitionChoice> LastCandidateChoices =>
        _lastCandidateChoices;

    private async Task<CandidateSelectionResolution> ResolveCandidateSelectionAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        string mapClass,
        CancellationToken cancellationToken,
        bool nativeChoicesPrepared = false,
        IReadOnlyList<Microsoft.UI.Xaml.Media.ImageSource?>? preloadedChoicePreviews = null,
        MapManualCandidateWindow.CandidateLivePreviewAssets? preloadedLivePreview = null,
        MapLearningScoreResult? precomputedLearningResult = null)
    {
        var scopedCandidates = candidates
            .Where(candidate => string.Equals(
                candidate.Recognition.Map.Class,
                mapClass,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var orderedCandidates = precomputedLearningResult is null
            ? scopedCandidates
                .OrderBy(candidate => candidate.IsReferenceOnly)
                .ThenBy(candidate => candidate.PreferredOrder)
                .ThenByDescending(candidate => candidate.RawConfidence)
                .ToArray()
            : scopedCandidates;
        if (_settings?.CandidateDecisionMode
                is MapCandidateDecisionMode.Fusion
                    or MapCandidateDecisionMode.ModelOnly
            && !nativeChoicesPrepared)
        {
            orderedCandidates = (await BuildNativeCandidateChoicesAsync(
                orderedCandidates,
                mapClass)).ToArray();
            nativeChoicesPrepared = true;
            preloadedChoicePreviews = null;
        }

        var learningResult = precomputedLearningResult
            ?? await _learningEngine.ScoreAsync(
                frame.Image,
                orderedCandidates,
                _settings?.CandidateDecisionMode
                    ?? MapCandidateDecisionMode.Traditional,
                cancellationToken);
        orderedCandidates = learningResult.Choices.ToArray();
        if (_headless && _activeCandidateSelector is not null)
        {
            // RealCLI replay cases carry a recorded map identity.  Recognition
            // intentionally presents only its bounded top candidates, so a
            // valid low-scoring fixture can otherwise be rejected before the
            // actual structure alignment is exercised.  Append catalog entries
            // only for headless selection; structure validation remains the
            // acceptance gate for these identity-only entries.
            orderedCandidates = (await BuildNativeCandidateChoicesAsync(
                orderedCandidates,
                mapClass)).ToArray();
        }
        // 预加载缓存以原候选顺序生成；候选窗口沿用排序后的顺序时同步重排，
        // 避免卡片标题与预览图错位。
        var orderedPreviews = ReorderChoicePreviews(
            candidates,
            orderedCandidates,
            preloadedChoicePreviews);
        _lastCandidateChoices = orderedCandidates;
        RememberMapLearningContext(frame, orderedCandidates, mapClass);
        if (CanAcceptModelTopOne(learningResult, orderedCandidates))
        {
            var recognition = MapCvRecognitionService.ConfirmChoice(
                orderedCandidates[0]);
            _statusMessage =
                $"空间模型高置信度建议：{recognition.Map.DisplayName}"
                + $" · {orderedCandidates[0].ModelMatchedFloorKey.ToUpperInvariant()}；"
                + "等待严格结构对齐。";
            var identityLock = LockSelectedMapIdentity(
                recognition,
                frame,
                userConfirmed: false);
            return new CandidateSelectionResolution(identityLock, false, recognition);
        }
        if (_activeCandidateSelector is not null)
        {
            return await SelectCandidateWithOverrideAsync(
                frame,
                orderedCandidates,
                reason,
                cancellationToken);
        }

        if (_headless)
        {
            var reliableCandidates = orderedCandidates
                .Where(candidate => !candidate.IsReferenceOnly)
                .ToArray();
            if (reliableCandidates.Length == 0)
            {
                _statusMessage = "未找到可验证的地图；Headless 模式不会采用待验证线索。";
                return new CandidateSelectionResolution(null, false);
            }
            var best = reliableCandidates[0];
            var recognition = MapCvRecognitionService.ConfirmChoice(best);
            _statusMessage =
                $"已自动选择可靠候选：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"Headless 自动候选选择 · map={recognition.Map.DisplayName} · confidence={recognition.Result.Confidence:P0}",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["confidence"] = recognition.Result.Confidence,
                    ["choiceCount"] = orderedCandidates.Length
                });
            var identityLock = LockSelectedMapIdentity(
                recognition,
                frame,
                userConfirmed: false);
            return new CandidateSelectionResolution(identityLock, false, recognition);
        }

        try
        {
            var displayChoices = nativeChoicesPrepared
                ? orderedCandidates
                : await BuildNativeCandidateChoicesAsync(
                    orderedCandidates,
                    mapClass);
            _lastCandidateChoices = displayChoices;
            var decision = await MapManualCandidateWindow.ShowAsync(
                frame,
                displayChoices,
                reason,
                cancellationToken,
                _captureProtection,
                _mapRepository,
                frame.ViewportBounds,
                orderedPreviews,
                preloadedLivePreview);
            if (decision.Kind == MapCandidateDecisionKind.StartSurvey)
                return new CandidateSelectionResolution(null, true);
            if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
                || decision.CandidateIndex is not { } index
                || index < 0
                || index >= displayChoices.Count)
            {
                _statusMessage = "用户取消了候选地图选择。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new CandidateSelectionResolution(null, false);
            }

            var recognition = MapCvRecognitionService.ConfirmChoice(displayChoices[index]);
            QueueHumanMapSelectionRecording(
                frame,
                displayChoices,
                recognition.Map.Id);
            _statusMessage = displayChoices[index].IsReferenceOnly
                ? $"正在严格复核参考线索：{recognition.Map.DisplayName}……"
                : $"用户选择了可靠候选：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
            var identityLock = LockSelectedMapIdentity(
                recognition,
                frame,
                userConfirmed: true);
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"候选地图已选择 · index={index} · map={recognition.Map.DisplayName}",
                details: new()
                {
                    ["selectedIndex"] = index,
                    ["choiceCount"] = displayChoices.Count,
                    ["mapId"] = recognition.Map.Id
                });
            return new CandidateSelectionResolution(identityLock, false, recognition);
        }
        catch (OperationCanceledException)
        {
            _statusMessage = "候选地图选择被取消。";
            return new CandidateSelectionResolution(null, false);
        }
    }

    private static IReadOnlyList<Microsoft.UI.Xaml.Media.ImageSource?>?
        ReorderChoicePreviews(
            IReadOnlyList<MapRecognitionChoice> source,
            IReadOnlyList<MapRecognitionChoice> ordered,
            IReadOnlyList<Microsoft.UI.Xaml.Media.ImageSource?>? previews)
    {
        if (previews is null || previews.Count != source.Count)
            return null;

        var result = new Microsoft.UI.Xaml.Media.ImageSource?[ordered.Count];
        for (var orderedIndex = 0; orderedIndex < ordered.Count; orderedIndex++)
        {
            for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
            {
                if (!ReferenceEquals(ordered[orderedIndex], source[sourceIndex]))
                    continue;
                result[orderedIndex] = previews[sourceIndex];
                break;
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<MapRecognitionChoice>>
        BuildNativeCandidateChoicesAsync(
            IReadOnlyList<MapRecognitionChoice> orderedCandidates,
            string mapClass)
    {
        var maps = await _mapRepository.GetMapsAsync();
        return MapCandidatePresentationRules.AppendCatalogMaps(
            orderedCandidates,
            maps,
            mapClass,
            _mapRepository.GetFloorOverlayPath);
    }

    private async Task<CandidateSelectionResolution> SelectCandidateWithOverrideAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken)
    {
        var selector = _activeCandidateSelector;
        if (selector is null)
            return new CandidateSelectionResolution(null, false);

        var decision = await selector.SelectAsync(
            frame,
            candidates,
            reason,
            cancellationToken);
        if (decision.Kind == MapCandidateDecisionKind.StartSurvey)
            return new CandidateSelectionResolution(null, true);
        if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
            || decision.CandidateIndex is not { } index
            || index < 0
            || index >= candidates.Count)
        {
            _statusMessage = "候选地图接口未返回有效选择。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new CandidateSelectionResolution(null, false);
        }

        var recognition = MapCvRecognitionService.ConfirmChoice(candidates[index]);
        QueueHumanMapSelectionRecording(
            frame,
            candidates,
            recognition.Map.Id);
        _statusMessage =
            $"候选地图接口已选择：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
        var identityLock = LockSelectedMapIdentity(
            recognition,
            frame,
            userConfirmed: true);
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"候选地图接口已选择 · position={index + 1} · map={recognition.Map.DisplayName}",
            details: new()
            {
                ["selectedIndex"] = index,
                ["selectedPosition"] = index + 1,
                ["choiceCount"] = candidates.Count,
                ["mapId"] = recognition.Map.Id,
                ["confidence"] = recognition.Result.Confidence
            });
        return new CandidateSelectionResolution(identityLock, false, recognition);
    }

    private bool CanAcceptModelTopOne(
        MapLearningScoreResult result,
        IReadOnlyList<MapRecognitionChoice> choices)
    {
        if (_settings?.RecognitionTuning.ForceCandidateSelection is not false
            || _settings.CandidateDecisionMode
                == MapCandidateDecisionMode.Traditional
            || !result.ModelAvailable
            || !result.ModelQualified
            || choices.Count == 0
            || choices[0].ModelProbability is not { } top
            || top < 0.85d)
        {
            return false;
        }

        var second = choices.Skip(1)
            .Select(choice => choice.ModelProbability)
            .FirstOrDefault(value => value.HasValue);
        return !second.HasValue || top - second.Value >= 0.15d;
    }

    private RuntimeMapRecognition LockSelectedMapIdentity(
        RuntimeMapRecognition selected,
        CapturedGameFrame frame,
        bool userConfirmed)
    {
        var floorKey = selected.Result.Floor;
        if (MapFloorRules.GetFloorProfile(selected.Map, floorKey) is null)
            floorKey = MapScanFloorRules.ResolveScanFloorKey(selected.Map);

        var identityLock = new RuntimeMapRecognition
        {
            Map = selected.Map,
            FloorImagePath = _mapRepository.GetFloorOverlayPath(
                selected.Map,
                floorKey),
            Result = new MapRecognitionResult
            {
                MapId = selected.Map.Id,
                Floor = floorKey,
                Confidence = selected.Result.Confidence,
                IdentityConfidence = 1d,
                LocalizationConfidence = 0d,
                Source = userConfirmed
                    ? MapRecognitionSource.UserConfirmed
                    : MapRecognitionSource.Automatic
            }
        };

        _lastRecognition = identityLock;
        _pendingAlignmentIdentity = identityLock;
        _mapLease.Bind(_matchSession.Snapshot, identityLock.Map.Id);
        _mapOpenSession.LockMapIdentity(
            identityLock.Map.Id,
            floorKey,
            identityLock.Result.IdentityConfidence);
        _currentFloorKey = floorKey;
        _lastGameBounds = frame.ClientBounds;
        _lastGameWindowHandle = frame.WindowHandle;
        _statusMessage =
            $"已锁定所选地图：{identityLock.Map.DisplayName} · "
            + $"{floorKey.ToUpperInvariant()}；正在首次对齐……";
        RefreshMiniMapForCurrentFloor();
        StateChanged?.Invoke(this, EventArgs.Empty);

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"{(userConfirmed ? "用户选择" : "合格模型建议")}后已立即锁定地图身份 · map={identityLock.Map.DisplayName} "
            + $"· floor={floorKey}",
            details: new()
            {
                ["mapId"] = identityLock.Map.Id,
                ["floor"] = floorKey,
                ["identityLocked"] = true,
                ["alignmentPending"] = true
            });

        return identityLock;
    }
}
/*
 * 文件职责：SessionOrchestrator.CandidateSelection。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
