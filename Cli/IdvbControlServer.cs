using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;

namespace IDVBuff.Cli;

internal sealed class IdvbControlHello
{
    public string Type { get; init; } = string.Empty;
    public string? OverlayGamePipeName { get; init; }
    public bool UseXButton1ForGameMap { get; init; }
}

internal sealed class IdvbControlCommand
{
    public string Type { get; init; } = string.Empty;
    public string? Input { get; init; }
}

internal sealed class IdvbControlReady
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int ProcessId { get; init; }
}

/// <summary>
/// Exposes the already running GUI SessionOrchestrator to an external RealCLI.
/// The pipe is a command bridge only: all commands still execute through the
/// same RealCliHost and the same production session instance.
/// </summary>
internal sealed class IdvbControlServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly DispatcherQueue _dispatcher;
    private readonly SessionOrchestrator _session;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public IdvbControlServer(
        string pipeName,
        DispatcherQueue dispatcher,
        SessionOrchestrator session)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("IDVB control pipe name is required.", nameof(pipeName));

        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _session = session;
    }

    public void Start()
    {
        if (_serverTask is not null)
            return;
        _serverTask = Task.Run(() => RunAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                await HandleClientAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A client may disconnect while a command is in flight. The
                // next connection must remain available.
            }
            catch (Exception exception)
            {
                Diagnostics.OutputLog.Write(
                    "ERROR",
                    "CLI/PIPE",
                    "IDVB control pipe stopped handling a client.",
                    exception);
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            server,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var helloLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(helloLine))
            return;

        IdvbControlHello? hello;
        try
        {
            hello = JsonSerializer.Deserialize<IdvbControlHello>(helloLine, CliJson.Options);
        }
        catch (JsonException exception)
        {
            await WriteAsync(writer, new IdvbControlReady
            {
                Error = exception.Message,
                ProcessId = Environment.ProcessId
            });
            return;
        }

        if (hello is null || !string.Equals(hello.Type, "hello", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(writer, new IdvbControlReady
            {
                Error = "The first IDVB control message must be a hello message.",
                ProcessId = Environment.ProcessId
            });
            return;
        }

        var options = new CliLaunchOptions
        {
            IsCli = true,
            OverlayGamePipeName = hello.OverlayGamePipeName,
            UseXButton1ForGameMap = hello.UseXButton1ForGameMap
        };
        await using var host = new RealCliHost(_session, options);

        try
        {
            await InvokeOnUiThreadAsync(
                () => host.StartRemoteAsync(cancellationToken),
                cancellationToken);
            await WriteAsync(writer, new IdvbControlReady
            {
                Ok = true,
                ProcessId = Environment.ProcessId
            });
        }
        catch (Exception exception)
        {
            await WriteAsync(writer, new IdvbControlReady
            {
                Error = exception.ToString(),
                ProcessId = Environment.ProcessId
            });
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var commandLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(commandLine))
                break;

            IdvbControlCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<IdvbControlCommand>(commandLine, CliJson.Options);
            }
            catch (JsonException exception)
            {
                await WriteAsync(writer, CliCommandResult.Failed(
                    "pipe",
                    exception.Message,
                    0,
                    exception: exception));
                continue;
            }

            if (command is null
                || !string.Equals(command.Type, "command", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(command.Input))
            {
                await WriteAsync(writer, CliCommandResult.Failed(
                    "pipe",
                    "Invalid IDVB control command.",
                    0));
                continue;
            }

            var result = await InvokeOnUiThreadAsync(
                () => host.ExecuteCommandAsync(command.Input, cancellationToken),
                cancellationToken);
            await WriteAsync(writer, result);

            if (string.Equals(command.Input.Trim(), "quit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command.Input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    private Task<T> InvokeOnUiThreadAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await operation());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new InvalidOperationException(
                "IDVB UI dispatcher is no longer available."));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private Task InvokeOnUiThreadAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await operation();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new InvalidOperationException(
                "IDVB UI dispatcher is no longer available."));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private static async Task WriteAsync<T>(StreamWriter writer, T value)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, CliJson.Options));
    }
}
