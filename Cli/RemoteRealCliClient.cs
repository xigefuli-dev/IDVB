using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Cli;

/// <summary>
/// Console-side RealCLI client for an already running IDVB GUI process.
/// It never starts, stops, or disposes the remote IDVB process.
/// </summary>
internal sealed class RemoteRealCliClient : IAsyncDisposable
{
    private readonly CliLaunchOptions _options;
    private readonly List<JsonElement> _results = [];
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _quitRequested;

    public RemoteRealCliClient(CliLaunchOptions options)
    {
        _options = options;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ConnectAsync(cancellationToken);

            if (_options.Command is not null)
            {
                var result = await SendCommandAsync(_options.Command, cancellationToken);
                Emit(result, machineReadable: _options.JsonOutput);
                return await FinishOutputAsync(GetExitCode(result));
            }

            if (_options.ScriptPath is not null)
                return await FinishOutputAsync(
                    await RunScriptAsync(_options.ScriptPath, cancellationToken));

            return await FinishOutputAsync(
                await RunInteractiveAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishOutputAsync(RealCliExitCodes.Fatal);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RealCLI 无法接管已运行的 IDVB：{exception}");
            return await FinishOutputAsync(RealCliExitCodes.Fatal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var pipeName = _options.IdvbAttachPipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidOperationException("未指定 IDVB control pipe。");

        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, cancellationToken);
        _reader = new StreamReader(
            _pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        _writer = new StreamWriter(
            _pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await WriteAsync(new
        {
            type = "hello",
            overlayGamePipeName = _options.OverlayGamePipeName,
            useXButton1ForGameMap = _options.UseXButton1ForGameMap
        });

        var ready = await ReadJsonAsync(cancellationToken);
        var ok = ready.TryGetProperty("ok", out var okValue)
            && okValue.ValueKind == JsonValueKind.True
            && okValue.GetBoolean();
        if (!ok)
        {
            var error = ready.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString()
                : "IDVB control pipe rejected the connection.";
            throw new InvalidOperationException(error);
        }
    }

    private async Task<int> RunScriptAsync(
        string scriptPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"脚本文件不存在：{scriptPath}");
            return RealCliExitCodes.BusinessFailure;
        }

        var exitCode = RealCliExitCodes.Success;
        foreach (var rawLine in await File.ReadAllLinesAsync(scriptPath, cancellationToken))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var result = await SendCommandAsync(line, cancellationToken);
            Emit(result, machineReadable: true);
            var commandExitCode = GetExitCode(result);
            if (commandExitCode == RealCliExitCodes.Fatal)
                return commandExitCode;
            if (commandExitCode != RealCliExitCodes.Success)
                exitCode = commandExitCode;
            if (_quitRequested)
                break;
        }
        return exitCode;
    }

    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Identity Vision Bridge / IDVB RealCLI");
        Console.WriteLine($"已接管 IDVB control pipe：{_options.IdvbAttachPipeName}");
        if (_options.OverlayGamePipeName is not null)
            Console.WriteLine($"已接管 overlay_game pipe：{_options.OverlayGamePipeName}");
        Console.WriteLine("输入 help 查看命令，quit 只退出 RealCLI，不关闭已接管的 IDVB 或 overlay_game。\n");

        var initial = await SendCommandAsync("status", cancellationToken);
        Emit(initial, machineReadable: false);
        while (!_quitRequested)
        {
            Console.Write("idvb> ");
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var result = await SendCommandAsync(line, cancellationToken);
            Emit(result, machineReadable: false);
        }
        return RealCliExitCodes.Success;
    }

    private async Task<JsonElement> SendCommandAsync(
        string input,
        CancellationToken cancellationToken)
    {
        await WriteAsync(new { type = "command", input });
        return await ReadJsonAsync(cancellationToken);
    }

    private async Task<JsonElement> ReadJsonAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
            throw new InvalidOperationException("RealCLI 尚未连接 IDVB。");
        var line = await _reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("IDVB control pipe 已断开。");
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private async Task WriteAsync<T>(T value)
    {
        if (_writer is null)
            throw new InvalidOperationException("RealCLI 尚未连接 IDVB。");
        await _writer.WriteLineAsync(JsonSerializer.Serialize(value, CliJson.Options));
    }

    private void Emit(JsonElement result, bool machineReadable)
    {
        _results.Add(result);
        var command = result.TryGetProperty("command", out var commandValue)
            ? commandValue.GetString()
            : "";
        _quitRequested = string.Equals(command, "quit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "exit", StringComparison.OrdinalIgnoreCase);

        if (machineReadable || _options.JsonOutput || _options.JsonLines)
        {
            Console.WriteLine(result.GetRawText());
            return;
        }

        var success = result.TryGetProperty("success", out var successValue)
            && successValue.ValueKind == JsonValueKind.True
            && successValue.GetBoolean();
        var status = result.TryGetProperty("statusMessage", out var statusValue)
            ? statusValue.GetString()
            : null;
        Console.WriteLine(success ? "[OK]" : "[FAIL]");
        Console.WriteLine($"{command}: {status}");
        if (!success && result.TryGetProperty("exception", out var exception)
            && exception.TryGetProperty("stackTrace", out var stackTrace))
            Console.WriteLine(stackTrace.GetString());

    }

    private async Task<int> FinishOutputAsync(int exitCode)
    {
        if (string.IsNullOrWhiteSpace(_options.OutputPath))
            return exitCode;

        try
        {
            var fullPath = Path.GetFullPath(_options.OutputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (_options.JsonLines)
            {
                await File.WriteAllLinesAsync(
                    fullPath,
                    _results.Select(result => result.GetRawText()));
            }
            else
            {
                var json = _results.Count == 1
                    ? _results[0].GetRawText()
                    : JsonSerializer.Serialize(_results, CliJson.Options);
                await File.WriteAllTextAsync(fullPath, json);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"无法保存 RealCLI 输出：{exception}");
            return RealCliExitCodes.Fatal;
        }
        return exitCode;
    }

    private static int GetExitCode(JsonElement result)
    {
        if (result.TryGetProperty("exitCode", out var value)
            && value.TryGetInt32(out var exitCode))
            return exitCode;
        return RealCliExitCodes.Fatal;
    }
}
