using System.Diagnostics;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

internal sealed record MapGpuSidecarDiagnostic(
    bool IsPrepared,
    string ExecutablePath,
    string Message);

public sealed record MapGpuInitializationResult(bool Succeeded,
    string Message,
    int ProcessId = 0,
    int ExitCode = 0,
    double ElapsedMilliseconds = 0d);

internal static partial class MapGpuTrainingSidecar
{
    private const string ProgressPrefix = "IDVB_MODEL_PROGRESS ";

    public static MapGpuSidecarDiagnostic Diagnose()
    {
        var executable = ResolveExecutable(Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            global::IDVBuff.AppDataPaths.RootDirectory);
        return executable is null
            ? new MapGpuSidecarDiagnostic(false, string.Empty,
                "未准备本地 GPU sidecar；训练将使用 CPU。")
            : new MapGpuSidecarDiagnostic(true, executable,
                $"已发现 GPU sidecar：{executable}");
    }

    internal static string? ResolveExecutable(string currentDirectory,
        string applicationBaseDirectory,
        string appDataRoot) => EnumerateExecutableCandidates(currentDirectory,
            applicationBaseDirectory, appDataRoot)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(File.Exists);

    private static IEnumerable<string> EnumerateExecutableCandidates(
        string currentDirectory,
        string applicationBaseDirectory,
        string appDataRoot)
    {
        yield return Path.Combine(currentDirectory, ".idvb-gpu",
            "runtime", "IDVB.RealCLI.exe");
        var directory = new DirectoryInfo(applicationBaseDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, ".idvb-gpu",
                "runtime", "IDVB.RealCLI.exe");
            directory = directory.Parent;
        }
        yield return Path.Combine(appDataRoot, "GpuRuntime", "IDVB.RealCLI.exe");
    }

    public static async Task<MapLearningTrainingResult> TrainAsync(
        string executablePath,
        string repositoryRoot,
        Action<MapLearningStatus> reportProgress,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(Path.GetTempPath(),
            $"idvb-gpu-train-{Guid.NewGuid():N}.json");
        var outputPath = requestPath + ".result.json";
        await File.WriteAllTextAsync(requestPath,
            "{\"schemaVersion\":1}", cancellationToken);
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = $"model-train --manifest \"{requestPath}\" "
                        + $"--repository \"{repositoryRoot}\" "
                        + $"--out \"{outputPath}\" "
                        + $"--parent-pid {Environment.ProcessId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            using var containment = SidecarJob.Create();
            containment.Assign(process);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken)
                is { } line)
            {
                if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                    continue;
                var status = JsonSerializer.Deserialize<MapLearningStatus>(
                    line[ProgressPrefix.Length..], JsonOptions);
                if (status is not null)
                    reportProgress(status);
            }
            await process.WaitForExitAsync(cancellationToken);
            var error = await stderrTask;
            if (process.ExitCode is not 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"GPU sidecar 退出码为 {process.ExitCode}。"
                    : error.Trim());
            }
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(outputPath, cancellationToken));
            return document.RootElement.GetProperty("result")
                .Deserialize<MapLearningTrainingResult>(JsonOptions)
                ?? throw new InvalidDataException("GPU sidecar 未返回训练结果。");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                process.Dispose();
            }
            TryDelete(requestPath);
            TryDelete(outputPath);
        }
    }

    public static async Task<MapGpuInitializationResult> InitializeAsync(
        string executablePath,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "model-device --warmup "
                        + $"--repository \"{repositoryRoot}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            using var containment = SidecarJob.Create();
            containment.Assign(process);
            var outputTask = process.StandardOutput.ReadToEndAsync(
                cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(
                cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                return new MapGpuInitializationResult(false,
                    string.IsNullOrWhiteSpace(error)
                        ? $"GPU 初始化进程退出码 {process.ExitCode}。"
                        : error.Trim(), process.Id, process.ExitCode,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var cuda = root.GetProperty("cudaAvailable").GetBoolean();
            var cudnn = root.GetProperty("cudnnAvailable").GetBoolean();
            var devices = root.GetProperty("deviceCount").GetInt32();
            var warmup = root.TryGetProperty("warmupMilliseconds", out var value)
                && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : 0d;
            return cuda && cudnn && devices > 0
                ? new MapGpuInitializationResult(true,
                    $"初始化完成：CUDA:0 · cuDNN 可用 · "
                        + $"设备 {devices} 个 · 热身 {warmup:F0} ms",
                    process.Id, process.ExitCode,
                    stopwatch.Elapsed.TotalMilliseconds)
                : new MapGpuInitializationResult(false,
                    $"GPU 运行时未就绪：CUDA={cuda}，cuDNN={cudnn}，"
                        + $"设备数={devices}。", process.Id, process.ExitCode,
                    stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                process.Dispose();
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
