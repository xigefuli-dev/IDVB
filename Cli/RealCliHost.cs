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
internal sealed partial class RealCliHost : IAsyncDisposable
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
                            scanOperationTrace = _session.LastScanOperationTrace,
                            alignmentOperationTrace = _session.LastAlignmentOperationTrace,
                            candidateOperationTrace = _session.LastCandidateOperationTrace,
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
        _ = ParseOptionInt(tokens, "--slot"); // Deprecated compatibility no-op.
        var mapClass = ParseOption(tokens, "--class") ?? "S1";

        await _session.BeginMatchAsync(mapClass);
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
}
