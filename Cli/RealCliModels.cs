using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.Features.Maps;

namespace IDVBuff.Cli;

internal static class RealCliExitCodes
{
    public const int Success = 0;
    public const int BusinessFailure = 10;
    public const int Fatal = 20;
}

internal sealed class CliLaunchOptions
{
    public bool IsCli { get; init; }
    public string? Command { get; init; }
    public string? ScriptPath { get; init; }
    public string? OverlayGamePath { get; init; }
    public string? OverlayGamePipeName { get; init; }
    public string? IdvbControlPipeName { get; init; }
    public string? IdvbAttachPipeName { get; init; }
    public string? OutputPath { get; init; }
    public bool StartOverlayGame { get; init; }
    public bool UseXButton1ForGameMap { get; init; }
    public bool JsonOutput { get; init; }
    public bool JsonLines { get; init; }
    public bool NoClear { get; init; }

    public static CliLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var command = default(string);
        var script = default(string);
        var overlayPath = default(string);
        var pipeName = default(string);
        var controlPipeName = default(string);
        var attachPipeName = default(string);
        var outputPath = default(string);
        var startOverlay = false;
        var useXButton1 = false;
        var json = false;
        var jsonLines = false;
        var noClear = false;
        var isCli = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--cli":
                    isCli = true;
                    break;
                case "--command":
                    command = ReadValue(args, ref i, "--command");
                    isCli = true;
                    break;
                case "--script":
                    script = ReadValue(args, ref i, "--script");
                    isCli = true;
                    break;
                case "--overlay-game":
                case "--overlay-game-path":
                    overlayPath = ReadValue(args, ref i, arg);
                    startOverlay = true;
                    isCli = true;
                    break;
                case "--overlay-game-pipe":
                    pipeName = ReadValue(args, ref i, arg);
                    isCli = true;
                    break;
                case "--idvb-control-pipe":
                    controlPipeName = ReadValue(args, ref i, arg);
                    break;
                case "--idvb-pipe":
                case "--attach-idvb":
                    attachPipeName = ReadValue(args, ref i, arg);
                    isCli = true;
                    break;
                case "--out":
                case "--output":
                    outputPath = ReadValue(args, ref i, arg);
                    isCli = true;
                    break;
                case "--start-overlay-game":
                    startOverlay = true;
                    isCli = true;
                    break;
                case "--game-map-xbutton1":
                    useXButton1 = true;
                    isCli = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--jsonl":
                    json = true;
                    jsonLines = true;
                    break;
                case "--no-clear":
                    noClear = true;
                    break;
            }
        }

        return new CliLaunchOptions
        {
            IsCli = isCli,
            Command = command,
            ScriptPath = script,
            OverlayGamePath = overlayPath,
            OverlayGamePipeName = pipeName,
            IdvbControlPipeName = controlPipeName,
            IdvbAttachPipeName = attachPipeName,
            OutputPath = outputPath,
            StartOverlayGame = startOverlay,
            UseXButton1ForGameMap = useXButton1,
            JsonOutput = json,
            JsonLines = jsonLines,
            NoClear = noClear
        };
    }

    private static string ReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}

internal sealed class CliExceptionInfo
{
    public string Type { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string StackTrace { get; init; } = string.Empty;

    public static CliExceptionInfo From(Exception exception) => new()
    {
        Type = exception.GetType().FullName ?? exception.GetType().Name,
        Message = exception.Message,
        StackTrace = exception.ToString()
    };
}

internal sealed record CliCommandResult
{
    public string Command { get; init; } = string.Empty;
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? StatusMessage { get; init; }
    public double ElapsedMs { get; init; }
    public object? Diagnostic { get; init; }
    public object? OverlayGame { get; init; }
    public IReadOnlyList<MapLogEntry>? CompleteLogs { get; init; }
    public CliExceptionInfo? Exception { get; init; }

    public static CliCommandResult Ok(
        string command,
        string? status,
        double elapsedMs,
        object? diagnostic = null,
        object? overlayGame = null) => new()
    {
        Command = command,
        Success = true,
        ExitCode = RealCliExitCodes.Success,
        StatusMessage = status,
        ElapsedMs = elapsedMs,
        Diagnostic = diagnostic,
        OverlayGame = overlayGame
    };

    public static CliCommandResult Failed(
        string command,
        string? status,
        double elapsedMs,
        object? diagnostic = null,
        Exception? exception = null,
        bool fatal = false,
        object? overlayGame = null) => new()
    {
        Command = command,
        Success = false,
        ExitCode = fatal ? RealCliExitCodes.Fatal : RealCliExitCodes.BusinessFailure,
        StatusMessage = status,
        ElapsedMs = elapsedMs,
        Diagnostic = diagnostic,
        OverlayGame = overlayGame,
        Exception = exception is null ? null : CliExceptionInfo.From(exception)
    };
}

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
