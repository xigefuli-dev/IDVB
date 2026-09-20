using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.RealCLI;

internal static class ModelMemoryTestCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var imagePath = ReadOption(args, "--image");
        var iterations = int.TryParse(ReadOption(args, "--iterations"),
            out var parsed) ? Math.Clamp(parsed, 20, 2000) : 200;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Console.Error.WriteLine(
                "错误：model-memory-test 需要 --image <path>。");
            return 1;
        }
        if (!torch.cuda.is_available())
        {
            Console.Error.WriteLine("错误：CUDA 不可用。");
            return 1;
        }

        try
        {
            using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
            using var network = new SiameseMapNetwork(torch.CUDA);
            network.EvaluationMode();
            using var noGrad = torch.no_grad();
            const int warmupIterations = 100;
            for (var index = 0; index < warmupIterations; index++)
                RunIteration(image, network);
            torch.cuda.synchronize();
            ForceCollection();
            var baseline = await CaptureAsync();
            var checkpoints = new List<MemorySnapshot>();
            var checkpointInterval = Math.Max(1, iterations / 10);
            for (var index = 0; index < iterations; index++)
            {
                RunIteration(image, network);
                if ((index + 1) % checkpointInterval == 0
                    || index + 1 == iterations)
                {
                    torch.cuda.synchronize();
                    ForceCollection();
                    checkpoints.Add(await CaptureAsync());
                }
            }
            var final = checkpoints[^1];
            var steadyBaseline = checkpoints[(checkpoints.Count / 2) - 1];
            var totalGrowth = Growth(baseline, final);
            var steadyGrowth = Growth(steadyBaseline, final);
            var passed = steadyGrowth.ManagedBytes <= 16L * 1024 * 1024
                && steadyGrowth.WorkingSetBytes <= 128L * 1024 * 1024
                && steadyGrowth.GpuBytes <= 64L * 1024 * 1024;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                passed,
                gpuMeasurement = "whole-device-WDDM",
                warmupIterations,
                iterations,
                thresholds = new
                {
                    managedBytes = 16L * 1024 * 1024,
                    workingSetBytes = 128L * 1024 * 1024,
                    gpuBytes = 64L * 1024 * 1024
                },
                totalGrowth,
                steadyStateGrowth = steadyGrowth,
                postWarmupBaseline = baseline,
                steadyStateBaseline = steadyBaseline,
                final,
                checkpoints
            }));
            return passed ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 3;
        }
    }

    private static void RunIteration(Mat image, SiameseMapNetwork network)
    {
        using var inputs = MapLearningPreprocessor.CreateGpuTrainingTensor(
            image, torch.CUDA);
        using var liveEmbeddings = network.EncodeLive(inputs);
        using var referenceEmbeddings = network.EncodeReference(inputs);
        using var logits = network.MatchEmbeddings(
            liveEmbeddings, referenceEmbeddings);
        using var probabilities = logits.sigmoid();
        _ = probabilities.sum().item<float>();
    }

    private static async Task<MemorySnapshot> CaptureAsync()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            await ReadGpuBytesAsync());
    }

    private static async Task<long> ReadGpuBytesAsync()
    {
        using var query = Process.Start(new ProcessStartInfo
        {
            FileName = "nvidia-smi.exe",
            Arguments = "--query-gpu=memory.used "
                + "--format=csv,noheader,nounits",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("无法启动 nvidia-smi。");
        var output = await query.StandardOutput.ReadToEndAsync();
        await query.WaitForExitAsync();
        foreach (var line in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(line.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var mebibytes))
                return (long)(mebibytes * 1024 * 1024);
        }
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name,
            StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static MemorySnapshot Growth(MemorySnapshot start,
        MemorySnapshot end) => new(
            end.ManagedBytes - start.ManagedBytes,
            end.WorkingSetBytes - start.WorkingSetBytes,
            end.GpuBytes - start.GpuBytes);

    private sealed record MemorySnapshot(long ManagedBytes,
        long WorkingSetBytes, long GpuBytes);
}
