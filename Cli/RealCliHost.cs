using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDVBuff.Features.Maps;

namespace IDVBuff.Cli;

/// <summary>
/// Interactive and scripted front end for the same SessionOrchestrator used
/// by the WinUI application.  This class contains presentation and protocol
/// code only; identification, alignment, capture and overlay rendering stay
/// in the production runtime.
/// </summary>
internal sealed class RealCliHost : IAsyncDisposable
{
    private static readonly Regex TokenRegex = new(
        "\\\"(?:\\\\.|[^\\\"])*\\\"|\\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SessionOrchestrator _session;
    private readonly CliLaunchOptions _options;
    private OverlayGameController? _overlayGame;
    private readonly List<CliCommandResult> _outputResults = [];
    private bool _quitRequested;
    private bool _disposed;

    public RealCliHost(SessionOrchestrator session, CliLaunchOptions options)
    {
        _session = session;
        _options = options;
    }

    internal async Task StartRemoteAsync(CancellationToken cancellationToken = default)
    {
        _session.EnableCliDiagnostics();
        if (_options.StartOverlayGame || _options.OverlayGamePipeName is not null)
            await EnsureOverlayGameAsync(cancellationToken);
    }

    internal Task<CliCommandResult> ExecuteCommandAsync(
        string input,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(input, cancellationToken);

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        _session.EnableCliDiagnostics();

        try
        {
            if (_options.StartOverlayGame || _options.OverlayGamePipeName is not null)
                await EnsureOverlayGameAsync(cancellationToken);

            if (_options.Command is not null)
            {
                var result = await ExecuteAsync(_options.Command, cancellationToken);
                Emit(result, machineReadable: _options.JsonOutput);
                return await FinishOutputAsync(result.ExitCode);
            }

            if (_options.ScriptPath is not null)
                return await FinishOutputAsync(
                    await RunScriptAsync(_options.ScriptPath, cancellationToken));

            return await FinishOutputAsync(
                await RunInteractiveAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CliCommandResult.Failed(
                "cancel",
                "RealCLI 已取消。",
                0,
                BuildSnapshot(),
                new OperationCanceledException("RealCLI operation was cancelled.", cancellationToken),
                overlayGame: _overlayGame?.LastState);
            Emit(cancelled, machineReadable: true);
            return await FinishOutputAsync(cancelled.ExitCode);
        }
        catch (Exception exception)
        {
            var result = CliCommandResult.Failed(
                "fatal",
                exception.Message,
                0,
                BuildSnapshot(),
                exception,
                fatal: true,
                overlayGame: _overlayGame?.LastState);
            Emit(result, machineReadable: true);
            return await FinishOutputAsync(result.ExitCode);
        }
    }

    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        await RenderTuiAsync(cancellationToken);
        while (!_quitRequested)
        {
            Console.Write("idvb> ");
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var result = await ExecuteAsync(line, cancellationToken);
            Emit(result, machineReadable: false);
            if (!_quitRequested)
                await RenderTuiAsync(cancellationToken);
        }
        return RealCliExitCodes.Success;
    }

    private async Task<int> RunScriptAsync(
        string scriptPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath))
        {
            var failure = CliCommandResult.Failed(
                "script",
                $"脚本文件不存在：{scriptPath}",
                0,
                BuildSnapshot());
            Emit(failure, machineReadable: true);
            return failure.ExitCode;
        }

        var exitCode = RealCliExitCodes.Success;
        foreach (var rawLine in await File.ReadAllLinesAsync(scriptPath, cancellationToken))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var result = await ExecuteAsync(line, cancellationToken);
            Emit(result, machineReadable: true);
            if (result.ExitCode == RealCliExitCodes.Fatal)
                return result.ExitCode;
            if (result.ExitCode != RealCliExitCodes.Success)
                exitCode = result.ExitCode;
            if (_quitRequested)
                break;
        }
        return exitCode;
    }

    private async Task<int> FinishOutputAsync(int exitCode)
    {
        if (string.IsNullOrWhiteSpace(_options.OutputPath))
            return exitCode;

        try
        {
            var completeLogs = await _session.LogCollector.GetCompleteEntriesAsync();
            var outputResults = _outputResults
                .Select(result => result with { CompleteLogs = completeLogs })
                .ToArray();
            var fullPath = Path.GetFullPath(_options.OutputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (_options.JsonLines)
            {
                var lines = _outputResults.Select(result =>
                    JsonSerializer.Serialize(
                        result with { CompleteLogs = completeLogs },
                        CliJson.Options));
                await File.WriteAllLinesAsync(fullPath, lines, cancellationToken: default);
            }
            else
            {
                object output = outputResults.Length == 1
                    ? outputResults[0]
                    : outputResults;
                await File.WriteAllTextAsync(
                    fullPath,
                    JsonSerializer.Serialize(output, CliJson.Options));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"无法保存 RealCLI 诊断输出：{exception}");
            return RealCliExitCodes.Fatal;
        }
        return exitCode;
    }

    private async Task<CliCommandResult> ExecuteAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            return CliCommandResult.Ok(input, _session.StatusMessage, 0, BuildSnapshot());

        var command = tokens[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "help":
                    return CliCommandResult.Ok(
                        input,
                        "可用命令：begin、scan、align、end、status、logs、timings、game、overlay、quit",
                        stopwatch.Elapsed.TotalMilliseconds,
                        new { commands = CommandHelp });

                case "quit":
                case "exit":
                    _quitRequested = true;
                    return CliCommandResult.Ok(
                        input,
                        "RealCLI 已请求退出。",
                        stopwatch.Elapsed.TotalMilliseconds,
                        BuildSnapshot());

                case "begin":
                    return await BeginAsync(input, tokens, stopwatch);
                case "scan":
                    return await ScanAsync(input, tokens, stopwatch, cancellationToken);
                case "align":
                    return await AlignAsync(input, stopwatch, cancellationToken);
                case "end":
                    await _session.EndMatchAsync();
                    var endOverlayState = await TryGetOverlayStateAsync(cancellationToken);
                    return CliCommandResult.Ok(
                        input,
                        _session.StatusMessage,
                        stopwatch.Elapsed.TotalMilliseconds,
                        BuildSnapshot(endOverlayState),
                        endOverlayState);
                case "status":
                    var statusOverlayState = await TryGetOverlayStateAsync(cancellationToken);
                    return CliCommandResult.Ok(
                        input,
                        _session.StatusMessage,
                        stopwatch.Elapsed.TotalMilliseconds,
                        BuildSnapshot(statusOverlayState),
                        statusOverlayState);
                case "logs":
                    var completeLogs = await _session.LogCollector.GetCompleteEntriesAsync();
                    var logsOverlayState = await TryGetOverlayStateAsync(cancellationToken);
                    return CliCommandResult.Ok(
                        input,
                        _session.StatusMessage,
                        stopwatch.Elapsed.TotalMilliseconds,
                        new
                        {
                            entries = completeLogs,
                            entryCount = completeLogs.Count,
                            pendingEntryCount = _session.LogCollector.BufferedEntryCount,
                            currentSessionPath = _session.LogCollector.CurrentSessionPath,
                            logDirectory = _session.LogCollector.LogDirectory
                        },
                        logsOverlayState);
                case "timings":
                    var timingsOverlayState = await TryGetOverlayStateAsync(cancellationToken);
                    return CliCommandResult.Ok(
                        input,
                        _session.StatusMessage,
                        stopwatch.Elapsed.TotalMilliseconds,
                        new
                        {
                            scanPhaseTimings = _session.LastScanPhaseTimings,
                            alignmentPhaseTimings = _session.LastAlignmentPhaseTimings,
                            diagnostics = _session.LastDiagnostics
                        },
                        timingsOverlayState);
                case "overlay":
                    return OverlayCommand(input, tokens, stopwatch);
                case "game":
                    return await GameCommandAsync(input, tokens, stopwatch, cancellationToken);
                default:
                    return CliCommandResult.Failed(
                        input,
                        $"未知命令：{tokens[0]}",
                        stopwatch.Elapsed.TotalMilliseconds,
                        BuildSnapshot());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CliCommandResult.Failed(
                input,
                exception.Message,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(),
                exception,
                fatal: false,
                overlayGame: _overlayGame?.LastState);
        }
    }

    private async Task<CliCommandResult> BeginAsync(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch)
    {
        var slot = ParseOptionInt(tokens, "--slot") ?? 1;
        var mapClass = ParseOption(tokens, "--class") ?? "S1";
        if (slot is < 1 or > 4)
            return CliCommandResult.Failed(
                input,
                "--slot 必须是 1 到 4。",
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot());

        await _session.BeginMatchAsync((PlayerSlot)slot, mapClass);
        var beginOverlayState = await TryGetOverlayStateAsync();
        if (beginOverlayState is not null)
        {
            _session.SynchronizeExternalGameMapState(beginOverlayState.MapOpen);
        }
        return CliCommandResult.Ok(
            input,
            _session.StatusMessage,
            stopwatch.Elapsed.TotalMilliseconds,
            BuildSnapshot(beginOverlayState),
            beginOverlayState);
    }

    private async Task<CliCommandResult> ScanAsync(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var candidateOption = ParseOption(tokens, "--candidate");
        int? candidatePosition = null;
        if (candidateOption is not null
            && (!int.TryParse(candidateOption, out var parsedPosition)
                || parsedPosition < 1))
        {
            return CliCommandResult.Failed(
                input,
                "--candidate 必须是从 1 开始的候选序号。",
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot());
        }
        if (candidateOption is not null)
            candidatePosition = int.Parse(candidateOption);

        if (!await PrepareCaptureTargetAsync(cancellationToken))
        {
            var overlayState = await TryGetOverlayStateAsync(cancellationToken);
            return CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(overlayState),
                overlayGame: overlayState);
        }
        var previous = _session.LastRecognition;
        await _session.RunQuickScanAsync(
            new RealCliCandidateSelector(candidatePosition));
        var succeeded = _session.LastRecognition is not null
            && !ReferenceEquals(previous, _session.LastRecognition);
        var currentOverlayState = await TryGetOverlayStateAsync(cancellationToken);
        return succeeded
            ? CliCommandResult.Ok(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                currentOverlayState)
            : CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                overlayGame: currentOverlayState);
    }

    private async Task<CliCommandResult> AlignAsync(
        string input,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!await PrepareCaptureTargetAsync(cancellationToken))
        {
            var overlayState = await TryGetOverlayStateAsync(cancellationToken);
            return CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(overlayState),
                overlayGame: overlayState);
        }
        var previous = _session.LastAlignmentSession;
        await _session.RunAlignmentAsync();
        var succeeded = _session.LastAlignmentSession is not null
            && !ReferenceEquals(previous, _session.LastAlignmentSession);
        var currentOverlayState = await TryGetOverlayStateAsync(cancellationToken);
        return succeeded
            ? CliCommandResult.Ok(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                currentOverlayState)
            : CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                overlayGame: currentOverlayState);
    }

    private CliCommandResult OverlayCommand(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch)
    {
        if (tokens.Count < 2)
            return CliCommandResult.Failed(input, "用法：overlay show|hide|toggle", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
        switch (tokens[1].ToLowerInvariant())
        {
            case "show":
                if (!_session.IsOverlayVisible)
                    _session.ToggleOverlay();
                return CliCommandResult.Ok(input, _session.StatusMessage, stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
            case "hide":
                if (_session.IsOverlayVisible)
                    _session.ToggleOverlay();
                return CliCommandResult.Ok(input, _session.StatusMessage, stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
            case "toggle":
                _session.ToggleOverlay();
                return CliCommandResult.Ok(input, _session.StatusMessage, stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
            default:
                return CliCommandResult.Failed(input, $"未知 overlay 命令：{tokens[1]}", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
        }
    }

    private async Task<CliCommandResult> GameCommandAsync(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (tokens.Count < 2)
            return CliCommandResult.Failed(input, "用法：game start|stop|status|map|map-only|floor|image|next|previous|clear", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());

        var operation = tokens[1].ToLowerInvariant();
        switch (operation)
        {
            case "start":
                return await GameStateResultAsync(
                    input,
                    await EnsureOverlayGameAsync(cancellationToken),
                    stopwatch);
            case "stop":
                if (_overlayGame is not null)
                {
                    var ownsProcess = _overlayGame.OwnsProcess;
                    if (ownsProcess)
                        await _overlayGame.StopAsync(cancellationToken);
                    else
                        _overlayGame.Disconnect();
                    await _overlayGame.DisposeAsync();
                    _overlayGame = null;
                    if (ownsProcess)
                        _session.SynchronizeExternalGameMapState(false);
                }
                return CliCommandResult.Ok(input, "overlay_game 已断开或关闭。", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
            case "status":
                return await GameStateResultAsync(input, await GetOverlayStateAsync(cancellationToken), stopwatch);
            case "map":
                return await GameStateResultAsync(
                    input,
                    await SetMapStateAsync(tokens, cancellationToken),
                    stopwatch);
            case "map-only":
                return await GameStateResultAsync(
                    input,
                    await SetMapOnlyStateAsync(tokens, cancellationToken),
                    stopwatch);
            case "floor":
                await EnsureOverlayGameAsync(cancellationToken);
                var floorState = await _overlayGame!.SelectFloorAsync(
                    ParseRequiredIndex(tokens, 2) - 1,
                    cancellationToken);
                _session.SelectFloorPosition(floorState.FloorIndex + 1);
                return await GameStateResultAsync(
                    input,
                    floorState,
                    stopwatch);
            case "image":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(
                    input,
                    await _overlayGame!.SelectImageAsync(ParseRequiredIndex(tokens, 2) - 1, cancellationToken),
                    stopwatch);
            case "next":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(input, await _overlayGame!.NextImageAsync(cancellationToken), stopwatch);
            case "previous":
            case "prev":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(input, await _overlayGame!.PreviousImageAsync(cancellationToken), stopwatch);
            case "clear":
                await EnsureOverlayGameAsync(cancellationToken);
                var cleared = await _overlayGame!.ClearImagesAsync(cancellationToken);
                _session.SynchronizeExternalGameMapState(cleared.MapOpen);
                return await GameStateResultAsync(input, cleared, stopwatch);
            default:
                return CliCommandResult.Failed(input, $"未知 game 命令：{tokens[1]}", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
        }
    }

    private async Task<OverlayGameState> SetMapStateAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        if (tokens.Count < 3)
            throw new ArgumentException("用法：game map open|close");
        var value = tokens[2].ToLowerInvariant();
        var open = value is "open" or "on" or "true";
        if (!open && value is not ("close" or "off" or "false"))
            throw new ArgumentException("game map 只能使用 open 或 close。");
        var state = await _overlayGame!.SetMapOpenAsync(open, cancellationToken);
        await Task.Delay(150, cancellationToken);
        _session.SynchronizeExternalGameMapState(state.MapOpen);
        if (open && !await _overlayGame.ActivateWindowAsync(cancellationToken))
            throw new InvalidOperationException("overlay_game 窗口无法置为前台，无法执行真实捕获。");
        return state;
    }

    private async Task<OverlayGameState> SetMapOnlyStateAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        if (tokens.Count < 3)
            throw new ArgumentException("用法：game map-only on|off");
        var value = tokens[2].ToLowerInvariant();
        var mapOnly = value is "on" or "true";
        if (!mapOnly && value is not ("off" or "false"))
            throw new ArgumentException("game map-only 只能使用 on 或 off。");
        return await _overlayGame!.SetMapOnlyAsync(mapOnly, cancellationToken);
    }

    private async Task<CliCommandResult> GameStateResultAsync(
        string input,
        OverlayGameState state,
        Stopwatch stopwatch) =>
        CliCommandResult.Ok(
            input,
            "overlay_game 状态已更新。",
            stopwatch.Elapsed.TotalMilliseconds,
            BuildSnapshot(state),
            state);

    private async Task<OverlayGameState> GetOverlayStateAsync(CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        return await _overlayGame!.GetStateAsync(cancellationToken);
    }

    private async Task<OverlayGameState> EnsureOverlayGameAsync(CancellationToken cancellationToken)
    {
        _overlayGame ??= new OverlayGameController(
            _options.OverlayGamePath,
            _options.OverlayGamePipeName);
        var state = await _overlayGame.StartAsync(cancellationToken);
        if (_options.UseXButton1ForGameMap)
            _session.UseCliGameMapXButton1Binding();
        _session.SynchronizeExternalGameMapState(state.MapOpen);
        return state;
    }

    private async Task<bool> PrepareCaptureTargetAsync(
        CancellationToken cancellationToken = default)
    {
        if (_overlayGame is null || !_overlayGame.IsConnected)
            return true;

        try
        {
            if (!await _overlayGame.ActivateWindowAsync(cancellationToken))
            {
                if (!_session.TryValidateCliCaptureTarget())
                    return false;
                _session.ReportCliCaptureFailure(
                    "overlay_game 窗口无法置为前台，无法执行真实捕获。");
                return false;
            }
            await Task.Delay(100, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            _session.ReportCliCaptureFailure(exception.Message);
            return false;
        }
    }

    private async Task<OverlayGameState?> TryGetOverlayStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_overlayGame is null || !_overlayGame.IsConnected)
            return null;
        return await _overlayGame.GetStateAsync(cancellationToken);
    }

    private Dictionary<string, object?> BuildSnapshot(
        OverlayGameState? overlayState = null)
    {
        var recognition = _session.LastRecognition;
        var map = recognition?.Map;
        var result = recognition?.Result;
        return new Dictionary<string, object?>
        {
            ["statusMessage"] = _session.StatusMessage,
            ["match"] = _session.MatchSnapshot,
            ["session"] = _session.SessionSnapshot,
            ["mapLocked"] = recognition is not null,
            ["map"] = map is null ? null : new
            {
                id = map.Id,
                displayName = map.DisplayName,
                floor = result?.Floor,
                confidence = result?.Confidence,
                source = result?.Source,
                hasTransform = result?.OverlayTransform is not null
            },
            ["candidates"] = _session.LastCandidateChoices
                .Select((choice, index) => new
                {
                    position = index + 1,
                    mapId = choice.Recognition.Map.Id,
                    mapDisplayName = choice.Recognition.Map.DisplayName,
                    floor = choice.Recognition.Result.Floor,
                    confidence = choice.RawConfidence,
                    vectorError = choice.VectorError
                })
                .ToArray(),
            ["gameMapOpen"] = _session.IsGameMapOpen,
            ["overlayVisible"] = _session.IsOverlayVisible,
            ["readyMapCount"] = _session.ReadyMapCount,
            ["totalMapCount"] = _session.TotalMapCount,
            ["scanPhaseTimings"] = _session.LastScanPhaseTimings,
            ["alignmentPhaseTimings"] = _session.LastAlignmentPhaseTimings,
            ["diagnostics"] = _session.LastDiagnostics,
            ["logSessionPath"] = _session.LogCollector.CurrentSessionPath,
            ["integrity"] = _session.IntegrityStatus,
            ["overlayGame"] = _overlayGame is null
                ? null
                : new
                {
                    connected = _overlayGame.IsConnected,
                    ownsProcess = _overlayGame.OwnsProcess,
                    pipeName = _overlayGame.PipeName,
                    lastStopResult = _overlayGame.LastStopResult,
                    state = overlayState ?? _overlayGame.LastState
                }
        };
    }

    private static IReadOnlyList<string> Tokenize(string input) =>
        TokenRegex.Matches(input)
            .Select(match => match.Value.Trim('"'))
            .ToArray();

    private static string? ParseOption(IReadOnlyList<string> tokens, string option)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], option, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        }
        return null;
    }

    private static int? ParseOptionInt(IReadOnlyList<string> tokens, string option)
    {
        var value = ParseOption(tokens, option);
        return int.TryParse(value, out var number) ? number : null;
    }

    private static int ParseRequiredIndex(IReadOnlyList<string> tokens, int index)
    {
        if (tokens.Count <= index || !int.TryParse(tokens[index], out var value) || value < 1)
            throw new ArgumentException("楼层和图片索引从 1 开始，必须是正整数。");
        return value;
    }

    private void Emit(CliCommandResult result, bool machineReadable)
    {
        _outputResults.Add(result);
        if (machineReadable || _options.JsonOutput || _options.JsonLines)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
            return;
        }

        Console.WriteLine(result.Success ? "[OK]" : $"[FAIL {result.ExitCode}]");
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            Console.WriteLine(result.StatusMessage);
        if (result.Exception is not null)
            Console.WriteLine(result.Exception.StackTrace);
    }

    private async Task RenderTuiAsync(CancellationToken cancellationToken)
    {
        if (!_options.NoClear && !Console.IsOutputRedirected)
        {
            try { Console.Clear(); } catch { }
        }

        var state = _session.LastRecognition;
        Console.WriteLine("Identity Vision Bridge / IDVB RealCLI");
        Console.WriteLine(new string('=', 44));
        Console.WriteLine($"对局：{(_session.MatchSnapshot.IsStarted ? "进行中" : "未开始")}");
        Console.WriteLine($"识别/对齐：{(_session.IsScanning ? "进行中" : "空闲")}");
        Console.WriteLine($"地图：{(state is null ? "未锁定" : state.Map.DisplayName)}");
        Console.WriteLine($"楼层：{_session.CurrentFloorKey ?? state?.Result.Floor ?? "-"}");
        Console.WriteLine($"游戏地图：{(_session.IsGameMapOpen ? "打开" : "关闭")}");
        Console.WriteLine($"IDVB Overlay：{(_session.IsOverlayVisible ? "显示" : "隐藏")}");
        Console.WriteLine($"overlay_game：{(_overlayGame?.IsConnected == true ? "已连接" : "未连接")}");
        Console.WriteLine($"状态：{_session.StatusMessage}");
        Console.WriteLine($"日志：{_session.LogCollector.EntryCount} 条，待写入 {_session.LogCollector.BufferedEntryCount} 条");
        var recentEntries = await _session.LogCollector
            .GetCompleteEntriesAsync(cancellationToken);
        foreach (var entry in recentEntries.TakeLast(3))
            Console.WriteLine($"  {entry.Level}/{entry.Category}: {entry.Message}");
        if (_session.LastScanPhaseTimings is { } scanTimings)
            Console.WriteLine($"扫描耗时：{string.Join(", ", scanTimings.Select(pair => $"{pair.Key}={pair.Value:0.##}ms"))}");
        if (_session.LastAlignmentPhaseTimings is { } alignmentTimings)
            Console.WriteLine($"对齐耗时：{string.Join(", ", alignmentTimings.Select(pair => $"{pair.Key}={pair.Value:0.##}ms"))}");
        Console.WriteLine();
        Console.WriteLine("begin --slot 1 --class S1 | scan [--candidate N] | align | end | status | logs | timings");
        Console.WriteLine("game start/status/map open|close/map-only on|off/floor N/image N/next/previous/clear");
        Console.WriteLine("quit");
    }

    private static readonly IReadOnlyDictionary<string, string> CommandHelp =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["begin"] = "begin --slot <1-4> --class <class>",
            ["scan"] = "scan [--candidate <1-N>]；RealCLI 默认优先选择结构已验证候选",
            ["align"] = "只对已锁定地图执行 IDVB 的仅对齐入口",
            ["end"] = "结束当前对局并释放锁定地图",
            ["status"] = "输出完整运行状态快照",
            ["logs"] = "输出 MapLogCollector 日志与持久化路径",
            ["timings"] = "输出扫描/对齐阶段耗时",
            ["game"] = "控制 overlay_game Named Pipe 状态",
            ["overlay"] = "使用 overlay show|hide|toggle 控制 IDVB Overlay",
            ["quit"] = "退出并清理子进程"
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_overlayGame is not null)
        {
            await _overlayGame.StopAsync();
            await _overlayGame.DisposeAsync();
            _overlayGame = null;
        }
    }
}
