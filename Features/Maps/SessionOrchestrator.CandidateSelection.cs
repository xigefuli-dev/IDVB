namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly record struct CandidateSelectionResolution(
        RuntimeMapRecognition? Recognition,
        bool StartSurvey);

    private IMapCandidateSelector? _activeCandidateSelector;
    private IReadOnlyList<MapRecognitionChoice> _lastCandidateChoices = [];

    public IReadOnlyList<MapRecognitionChoice> LastCandidateChoices =>
        _lastCandidateChoices;

    private async Task<CandidateSelectionResolution> ResolveCandidateSelectionAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken)
    {
        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.IsReferenceOnly)
            .ThenBy(candidate => candidate.PreferredOrder)
            .ThenByDescending(candidate => candidate.RawConfidence)
            .ToArray();
        _lastCandidateChoices = orderedCandidates;
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
            return new CandidateSelectionResolution(recognition, false);
        }

        try
        {
            var decision = await MapManualCandidateWindow.ShowAsync(
                frame,
                orderedCandidates,
                reason,
                cancellationToken);
            if (decision.Kind == MapCandidateDecisionKind.StartSurvey)
                return new CandidateSelectionResolution(null, true);
            if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
                || decision.CandidateIndex is not { } index
                || index < 0
                || index >= orderedCandidates.Length)
            {
                _statusMessage = "用户取消了候选地图选择。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new CandidateSelectionResolution(null, false);
            }

            var recognition = MapCvRecognitionService.ConfirmChoice(orderedCandidates[index]);
            _statusMessage = orderedCandidates[index].IsReferenceOnly
                ? $"正在严格复核参考线索：{recognition.Map.DisplayName}……"
                : $"用户选择了可靠候选：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"候选地图已选择 · index={index} · map={recognition.Map.DisplayName}",
                details: new()
                {
                    ["selectedIndex"] = index,
                    ["choiceCount"] = orderedCandidates.Length,
                    ["mapId"] = recognition.Map.Id
                });
            return new CandidateSelectionResolution(recognition, false);
        }
        catch (OperationCanceledException)
        {
            _statusMessage = "候选地图选择被取消。";
            return new CandidateSelectionResolution(null, false);
        }
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
        _statusMessage =
            $"候选地图接口已选择：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
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
        return new CandidateSelectionResolution(recognition, false);
    }
}
