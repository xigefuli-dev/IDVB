using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace IDVBuff.Cli;

internal sealed class OverlayGameState
{
    public bool MapOpen { get; init; }
    public bool MapOnly { get; init; }
    public int FloorIndex { get; init; }
    public string Floor { get; init; } = string.Empty;
    public int ImageIndex { get; init; }
    public int ImageCount { get; init; }
    public string? ImagePath { get; init; }
    public long ToggleCount { get; init; }
    public long WindowHandle { get; init; }
    public int ProcessId { get; init; }

    public static OverlayGameState FromJson(JsonElement state)
    {
        return new OverlayGameState
        {
            MapOpen = GetBool(state, "mapOpen"),
            MapOnly = GetBool(state, "mapOnly"),
            FloorIndex = GetInt(state, "floorIndex"),
            Floor = GetString(state, "floor") ?? string.Empty,
            ImageIndex = GetInt(state, "imageIndex"),
            ImageCount = GetInt(state, "imageCount"),
            ImagePath = GetString(state, "imagePath"),
            ToggleCount = GetLong(state, "toggleCount"),
            WindowHandle = GetLong(state, "hwnd"),
            ProcessId = GetInt(state, "pid")
        };
    }

    private static bool GetBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private static int GetInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var number)
            ? number
            : 0;

    private static long GetLong(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
        && property.TryGetInt64(out var number)
            ? number
            : 0;

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal sealed class OverlayGameResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public OverlayGameState? State { get; init; }

    public static OverlayGameResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var success = root.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.True;
        var error = root.TryGetProperty("error", out var errorValue)
            ? errorValue.GetString()
            : null;
        var state = root.TryGetProperty("state", out var stateValue)
            && stateValue.ValueKind == JsonValueKind.Object
            ? OverlayGameState.FromJson(stateValue)
            : null;
        return new OverlayGameResponse
        {
            Success = success,
            Error = error,
            State = state
        };
    }
}

/// <summary>
/// Real overlay_game process controller.  It intentionally knows only the
/// external protocol; all map state changes remain inside overlay_game's UI
/// thread and all IDVB capture remains inside DwrGameWindowCaptureService.
/// </summary>
internal sealed class OverlayGameController : IAsyncDisposable
{
    private readonly string? _requestedPath;
    private readonly string? _requestedPipeName;
    private Process? _ownedProcess;
    private string? _pipeName;
    private bool _disposed;
    private OverlayGameState? _lastState;

    public OverlayGameController(string? path, string? pipeName)
    {
        _requestedPath = path;
        _requestedPipeName = pipeName;
    }

    public bool IsConnected => !string.IsNullOrWhiteSpace(_pipeName);
    public bool OwnsProcess => _ownedProcess is not null;
    public string? PipeName => _pipeName;
    public Process? Process => _ownedProcess;
    public OverlayGameState? LastState => _lastState;
    public string? LastStopResult { get; private set; }

    public async Task<OverlayGameState> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(_pipeName))
            return await GetStateAsync(cancellationToken);

        _pipeName = _requestedPipeName
            ?? $"IDVB.OverlayGame.{Environment.ProcessId}.{Guid.NewGuid():N}";

        // A named pipe is always an attach-only mode.  A path is used only
        // when this controller created the pipe name itself.
        if (_requestedPipeName is null)
        {
            var executable = ResolveExecutable(_requestedPath);
            if (executable is null)
                throw new FileNotFoundException(
                    "找不到 overlay_game/dwrg.exe。请通过 --overlay-game 指定路径。",
                    _requestedPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(_pipeName);
            _ownedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 overlay_game/dwrg.exe。");
        }

        var deadline = Stopwatch.GetTimestamp()
            + (long)(Stopwatch.Frequency * 15d);
        Exception? lastError = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ownedProcess?.HasExited is true)
                throw new InvalidOperationException(
                    $"overlay_game 已退出，退出码 {_ownedProcess.ExitCode}。",
                    lastError);
            try
            {
                return await GetStateAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is TimeoutException
                or IOException
                or InvalidOperationException)
            {
                lastError = exception;
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"等待 overlay_game Named Pipe readiness 超时：{_pipeName}。",
            lastError);
    }

    public Task<OverlayGameState> GetStateAsync(CancellationToken ct = default) =>
        SendStateCommandAsync("get_state", null, ct);

    public Task<OverlayGameState> SetMapOpenAsync(bool open, CancellationToken ct = default) =>
        SendStateCommandAsync("set_map_open", new { open }, ct);

    public Task<OverlayGameState> SetMapOnlyAsync(bool mapOnly, CancellationToken ct = default) =>
        SendStateCommandAsync("set_map_only", new { mapOnly }, ct);

    public Task<OverlayGameState> SelectFloorAsync(int floorIndex, CancellationToken ct = default) =>
        SendStateCommandAsync("select_floor", new { floorIndex }, ct);

    public Task<OverlayGameState> SelectImageAsync(int imageIndex, CancellationToken ct = default) =>
        SendStateCommandAsync("select_image", new { imageIndex }, ct);

    public Task<OverlayGameState> NextImageAsync(CancellationToken ct = default) =>
        SendStateCommandAsync("next_image", null, ct);

    public Task<OverlayGameState> PreviousImageAsync(CancellationToken ct = default) =>
        SendStateCommandAsync("previous_image", null, ct);

    public Task<OverlayGameState> ClearImagesAsync(CancellationToken ct = default) =>
        SendStateCommandAsync("clear_images", null, ct);

    public async Task<bool> ActivateWindowAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken);
        if (state.WindowHandle == 0)
            return false;

        var window = new IntPtr(state.WindowHandle);
        if (!IsWindow(window))
            return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetForegroundWindow(window);
            if (GetForegroundWindow() == window)
                return true;
            await Task.Delay(50, cancellationToken);
        }
        return GetForegroundWindow() == window;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        if (_ownedProcess is null)
        {
            if (string.IsNullOrWhiteSpace(_pipeName))
            {
                LastStopResult ??= "already_stopped";
                return;
            }

            // An attached process belongs to the caller.  Disconnecting must
            // never send it a shutdown command.
            _pipeName = null;
            _lastState = null;
            LastStopResult = "external_disconnected";
            return;
        }

        var shutdownAcknowledged = false;
        if (!string.IsNullOrWhiteSpace(_pipeName))
        {
            try
            {
                await SendStateCommandAsync("shutdown", null, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                shutdownAcknowledged = true;
            }
            catch when (_ownedProcess is not null)
            {
                // The process may already have closed its pipe while shutting down.
            }
        }

        if (_ownedProcess is { } process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(
                    TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                KillOwnedProcess(process, "owned_killed_after_cancellation");
            }
            catch (TimeoutException)
            {
                KillOwnedProcess(process, "owned_killed_after_shutdown_timeout");
            }
            finally
            {
                process.Dispose();
                _ownedProcess = null;
            }
        }

        _pipeName = null;
        _lastState = null;
        LastStopResult ??= shutdownAcknowledged
            ? "owned_shutdown_acknowledged"
            : "owned_process_reaped";
    }

    public void Disconnect()
    {
        if (_ownedProcess is not null)
            throw new InvalidOperationException("不能断开由当前 RealCLI 管理的 overlay_game 进程。");
        _pipeName = null;
        LastStopResult = "external_disconnected";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync();
        _disposed = true;
    }

    private void KillOwnedProcess(Process process, string result)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            LastStopResult = result;
        }
        catch (Exception exception)
        {
            LastStopResult = $"{result}_failed:{exception.GetType().Name}";
        }
    }

    private async Task<OverlayGameState> SendStateCommandAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pipeName))
            throw new InvalidOperationException("overlay_game 尚未连接。");

        var request = JsonSerializer.Serialize(
            new { command, args = arguments },
            CliJson.Options);
        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(1500, cancellationToken);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        await writer.WriteLineAsync(request);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var responseJson = await reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("overlay_game Named Pipe 返回空响应。");
        var response = OverlayGameResponse.Parse(responseJson);
        if (!response.Success || response.State is null)
            throw new InvalidOperationException(
                response.Error ?? $"overlay_game 命令失败：{command}");
        _lastState = response.State;
        return response.State;
    }

    private static string? ResolveExecutable(string? requestedPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            candidates.Add(Path.GetFullPath(requestedPath));
            if (!Path.HasExtension(requestedPath))
                candidates.Add(Path.GetFullPath(Path.Combine(requestedPath, "dwrg.exe")));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "dwrg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Tools", "overlay_game", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "Tools", "overlay_game", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "Tools", "overlay_game", "build", "Release", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "Tools", "overlay_game", "native", "build", "Release", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "Tools", "overlay_game", "native", "build", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, ".verify", "overlay-game", "native", "build", "dwrg.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, ".verify", "overlay_game", "native", "build", "dwrg.exe"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}
