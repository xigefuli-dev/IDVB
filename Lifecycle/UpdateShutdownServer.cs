using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.Lifecycle;

internal sealed class UpdateShutdownServer : IAsyncDisposable
{
    private readonly Action _requestShutdown;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public UpdateShutdownServer(Action requestShutdown)
    {
        _requestShutdown = requestShutdown;
    }

    public void Start() => _serverTask ??= Task.Run(() => RunAsync(_shutdown.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                UpdateProtocol.ShutdownPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                await HandleRequestAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
            catch (Exception exception)
            {
                Diagnostics.OutputLog.Write("ERROR", "UPDATE/PIPE", "Update shutdown request failed.", exception);
            }
        }
    }

    private async Task HandleRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        var line = await reader.ReadLineAsync(cancellationToken);
        UpdateShutdownRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<UpdateShutdownRequest>(line ?? string.Empty, UpdateProtocol.JsonOptions);
        }
        catch (JsonException)
        {
        }

        var error = ValidateRequest(request);
        var response = new UpdateShutdownResponse(
            UpdateProtocol.PipeSchemaVersion,
            error is null,
            Environment.ProcessId,
            error);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, UpdateProtocol.JsonOptions));
        if (error is null)
            _requestShutdown();
    }

    private static string? ValidateRequest(UpdateShutdownRequest? request)
    {
        if (request is null
            || request.SchemaVersion != UpdateProtocol.PipeSchemaVersion
            || !string.Equals(request.Type, "prepare_shutdown", StringComparison.Ordinal)
            || request.UpdaterProcessId <= 0)
            return "无效的更新关闭请求。";
        try
        {
            using var process = Process.GetProcessById(request.UpdaterProcessId);
            var processPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(processPath)
                || !string.Equals(Path.GetFileName(processPath), "IDVB.Updater.exe", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(processPath).StartsWith(
                    Path.GetFullPath(AppContext.BaseDirectory),
                    StringComparison.OrdinalIgnoreCase))
                return "更新器进程不属于当前 IDVB 安装目录。";
        }
        catch
        {
            return "无法验证更新器进程。";
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
        _shutdown.Dispose();
    }
}
