using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>One persistent worker; C# remains the owner of map/open state.</summary>
internal sealed class ReferencePythonEngineClient : IDisposable, IAsyncDisposable
{
    private const string ConfigVariable = "IDVB_PYTHON_ENGINE_CONFIG";
    private const int MaximumStderrCharacters = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _startupTask;
    private Task? _stderrTask;
    private Task? _shutdownTask;
    private Exception? _workerFailure;
    private bool _disposed;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigVariable));

    internal bool IsReady
    {
        get { lock (_stateGate) return !_disposed && _startupTask?.IsCompletedSuccessfully == true && _workerFailure is null; }
    }

    // Kept locally and bounded; never copy command lines or environment values
    // into exceptions, and never forward arbitrary stderr to protocol stdout.
    internal string StandardErrorTail
    {
        get { lock (_stateGate) return _stderr.ToString(); }
    }

    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task startup;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startupTask is null)
            {
                _startupTask = StartWorkerAsync();
                ObserveFailure(_startupTask);
            }
            startup = _startupTask;
        }
        // Cancelling one observer must not abort startup for another map-open.
        return startup.WaitAsync(cancellationToken);
    }

    public Task<ReferencePythonEngineResponse> InvokeAsync(Mat fullClientBgr,
        object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate) ObjectDisposedException.ThrowIf(_disposed, this);
        var message = JsonSerializer.SerializeToNode(request, JsonOptions) as JsonObject
            ?? throw new ArgumentException("Python 引擎请求必须是 JSON 对象。", nameof(request));
        var operation = message["operation"]?.GetValue<string>();
        if (operation is not ("identify" or "align"))
            throw new ArgumentException("Python 引擎仅接受 identify 或 align 帧请求。", nameof(request));

        // Copy before the first await. The caller may dispose its Mat as soon
        // as a cancelled await returns, while the worker is still reading.
        var frame = SharedFrame.Create(fullClientBgr);
        message["id"] = Guid.NewGuid().ToString("N");
        message["frame"] = JsonSerializer.SerializeToNode(new
        {
            coordinateSpace = "client", memoryName = frame.Name,
            width = frame.Width, height = frame.Height, stride = frame.Stride
        }, JsonOptions);
        var pending = InvokeOwnedFrameAsync(frame, message, cancellationToken);
        ObserveFailure(pending);
        return pending.WaitAsync(cancellationToken);
    }

    private async Task<ReferencePythonEngineResponse> InvokeOwnedFrameAsync(
        SharedFrame frame, JsonObject message, CancellationToken cancellationToken)
    {
        using (frame)
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Process process;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_workerFailure is not null)
                        throw new IOException("参考 Python 引擎不可用。", _workerFailure);
                    process = _process ?? throw new IOException("参考 Python 引擎尚未启动。");
                }
                // From the first write until the matching response, neither
                // the caller's cancellation nor a new request owns this gate.
                return await ExchangeAsync(process, message).ConfigureAwait(false);
            }
            finally { _requestGate.Release(); }
        }
    }

    private async Task StartWorkerAsync()
    {
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = LoadConfiguration();
            var start = new ProcessStartInfo
            {
                FileName = configuration.PythonExecutable,
                WorkingDirectory = configuration.RuntimeRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add(configuration.WorkerScript);
            start.ArgumentList.Add("--source-root");
            start.ArgumentList.Add(configuration.SourceRoot);
            start.ArgumentList.Add("--runtime-root");
            start.ArgumentList.Add(configuration.RuntimeRoot);
            start.ArgumentList.Add("--catalog-path");
            start.ArgumentList.Add(configuration.CatalogPath);
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            start.Environment["PYTHONUNBUFFERED"] = "1";
            start.Environment["PYTHONUTF8"] = "1";
            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("参考 Python 引擎启动失败。");
            }
            catch { process.Dispose(); throw; }
            lock (_stateGate)
            {
                _process = process;
                _stderrTask = CollectStderrAsync(process.StandardError);
            }
            var ready = await ExchangeAsync(process, new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["operation"] = "ping"
            }).ConfigureAwait(false);
            if (ready.Ready != true && ready.Ok != true)
                throw new InvalidDataException("参考 Python 引擎未确认就绪。");
        }
        catch (Exception exception)
        {
            lock (_stateGate) _workerFailure ??= exception;
            throw;
        }
        finally { _requestGate.Release(); }
    }

    private async Task<ReferencePythonEngineResponse> ExchangeAsync(
        Process process, JsonObject message)
    {
        ReferencePythonEngineResponse response;
        try
        {
            await process.StandardInput.WriteLineAsync(message.ToJsonString(JsonOptions)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)
                ?? throw new EndOfStreamException("参考 Python 引擎在返回结果前退出。");
            response = JsonSerializer.Deserialize<ReferencePythonEngineResponse>(line, JsonOptions)
                ?? throw new InvalidDataException("参考 Python 引擎返回了空响应。");
            if (!string.Equals(response.Id, message["id"]!.GetValue<string>(), StringComparison.Ordinal)
                || !string.Equals(response.Operation, message["operation"]!.GetValue<string>(), StringComparison.Ordinal))
                throw new InvalidDataException("参考 Python 引擎响应与当前请求不匹配。");
        }
        catch (Exception exception)
        {
            lock (_stateGate) _workerFailure ??= exception;
            throw new IOException("参考 Python 引擎通信失败。", exception);
        }
        if (response.Ok == false || !string.IsNullOrWhiteSpace(response.Error))
            throw new InvalidOperationException("参考 Python 引擎拒绝请求："
                + Limit(response.Error ?? response.Reason ?? "worker-error", 512));
        return response;
    }

    private async Task CollectStderrAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                lock (_stateGate)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > MaximumStderrCharacters)
                        _stderr.Remove(0, _stderr.Length - MaximumStderrCharacters);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // stderr is diagnostic data; stdout owns the transport outcome.
        }
    }

    public void Dispose() => ObserveFailure(BeginShutdown());
    public ValueTask DisposeAsync() => new(BeginShutdown());

    private Task BeginShutdown()
    {
        lock (_stateGate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _disposed = true;
            return _shutdownTask = StopWorkerAsync(_startupTask);
        }
    }

    private async Task StopWorkerAsync(Task? startup)
    {
        if (startup is not null)
            try { await startup.ConfigureAwait(false); } catch { }
        await _requestGate.WaitAsync().ConfigureAwait(false);
        Process? process;
        try
        {
            lock (_stateGate) process = _process;
            if (process is null) return;
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        id = Guid.NewGuid().ToString("N"), operation = "shutdown"
                    }, JsonOptions)).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
            finally { process.StandardInput.Close(); }
        }
        finally { _requestGate.Release(); }
        // Only this client's child is signalled, and only through its protocol.
        // Do not kill a process to make cancellation or application exit fast.
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (_stderrTask is { } stderr) await stderr.ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            lock (_stateGate) _process = null;
        }
    }

    private static Configuration LoadConfiguration()
    {
        var path = Environment.GetEnvironmentVariable(ConfigVariable);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("未配置参考 Python 引擎的绝对配置路径。");
        var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("参考 Python 引擎配置为空。");
        RequirePath(configuration.PythonExecutable, directory: false, "PythonExecutable");
        RequirePath(configuration.WorkerScript, directory: false, "WorkerScript");
        RequirePath(configuration.SourceRoot, directory: true, "SourceRoot");
        RequirePath(configuration.RuntimeRoot, directory: true, "RuntimeRoot");
        RequirePath(configuration.CatalogPath, directory: false, "CatalogPath");
        return configuration;
    }

    private static void RequirePath(string path, bool directory, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !(directory ? Directory.Exists(path) : File.Exists(path)))
            throw new InvalidDataException($"参考 Python 引擎配置 {name} 必须指向现有绝对路径。");
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(
        completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private sealed class Configuration
    {
        public string PythonExecutable { get; init; } = string.Empty;
        public string WorkerScript { get; init; } = string.Empty;
        public string SourceRoot { get; init; } = string.Empty;
        public string RuntimeRoot { get; init; } = string.Empty;
        public string CatalogPath { get; init; } = string.Empty;
    }

    private sealed class SharedFrame : IDisposable
    {
        private readonly MemoryMappedFile _memory;
        private SharedFrame(MemoryMappedFile memory, string name, int width, int height, int stride)
        { _memory = memory; Name = name; Width = width; Height = height; Stride = stride; }
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }

        public static SharedFrame Create(Mat image)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Empty() || image.Type() != MatType.CV_8UC3)
                throw new ArgumentException("Python 引擎需要非空 UInt8 BGR 客户区图像。", nameof(image));
            var width = image.Width;
            var height = image.Height;
            var stride = checked(width * 3);
            var capacity = checked((long)stride * height);
            var name = $"IDVB.ReferencePython.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var memory = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
            try
            {
                using var view = memory.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.Write);
                var row = new byte[stride];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(image.Ptr(y), row, 0, stride);
                    view.WriteArray((long)y * stride, row, 0, stride);
                }
                return new SharedFrame(memory, name, width, height, stride);
            }
            catch { memory.Dispose(); throw; }
        }

        public void Dispose() => _memory.Dispose();
    }
}
