namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private IMapCandidateSelector? _activeCandidateSelector;
    private IReadOnlyList<MapRecognitionChoice> _lastCandidateChoices = [];

    public IReadOnlyList<MapRecognitionChoice> LastCandidateChoices =>
        _lastCandidateChoices;

    private async Task<RuntimeMapRecognition?> ResolveCandidateSelectionAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken)
    {
        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.RawConfidence)
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
            var best = orderedCandidates[0];
            var recognition = MapCvRecognitionService.ConfirmChoice(best);
            _statusMessage =
                $"已自动选择候选地图：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
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
            return recognition;
        }

        try
        {
            var selectedIndex = await MapManualCandidateWindow.ShowAsync(
                frame,
                orderedCandidates,
                reason,
                cancellationToken);
            if (selectedIndex is not { } index || index >= orderedCandidates.Length)
            {
                _statusMessage = "用户取消了候选地图选择。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return null;
            }

            var recognition = MapCvRecognitionService.ConfirmChoice(orderedCandidates[index]);
            _statusMessage =
                $"用户选择了候选地图：{recognition.Map.DisplayName} · 置信度 {recognition.Result.Confidence:P0}";
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
            return recognition;
        }
        catch (OperationCanceledException)
        {
            _statusMessage = "候选地图选择被取消。";
            return null;
        }
    }

    private async Task<RuntimeMapRecognition?> SelectCandidateWithOverrideAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken)
    {
        var selector = _activeCandidateSelector;
        if (selector is null)
            return null;

        var selectedIndex = await selector.SelectAsync(
            frame,
            candidates,
            reason,
            cancellationToken);
        if (selectedIndex is not { } index
            || index < 0
            || index >= candidates.Count)
        {
            _statusMessage = "候选地图接口未返回有效选择。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
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
        return recognition;
    }
}
