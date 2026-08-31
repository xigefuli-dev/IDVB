using System.Text.Json;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Output;

namespace IDVBuff.RealCLI;

internal static class ModelTrainCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? manifestPath = null;
        string? repository = null;
        string? outputPath = null;
        int? parentProcessId = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--manifest":
                case "-m": manifestPath = args[++index]; break;
                case "--repository": repository = args[++index]; break;
                case "--out":
                case "-o": outputPath = args[++index]; break;
                case "--parent-pid":
                    parentProcessId = int.Parse(args[++index]); break;
            }
        }
        if (string.IsNullOrWhiteSpace(manifestPath)
            || !File.Exists(manifestPath)
            || string.IsNullOrWhiteSpace(repository))
        {
            Console.Error.WriteLine(
                "错误：model-train 需要 --manifest 和 --repository。");
            return 1;
        }

        try
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(manifestPath));
            var schema = document.RootElement.TryGetProperty(
                "schemaVersion", out var value)
                    ? value.GetInt32()
                    : 1;
            if (schema is < 1 or > 1)
                throw new InvalidDataException($"不支持训练 schemaVersion={schema}。");

            await using var engine = new MapCandidateLearningEngine(repository);
            await engine.InitializeAsync();
            using var trainingCancellation = new CancellationTokenSource();
            var parentMonitor = parentProcessId is int parentId
                ? MonitorParentAsync(parentId, trainingCancellation)
                : Task.CompletedTask;
            var trainingTask = engine.TrainNowAsync(trainingCancellation.Token);
            MapLearningStatus? lastStatus = null;
            while (!trainingTask.IsCompleted)
            {
                var status = engine.Status;
                if (lastStatus is null
                    || status.TrainingPhase != lastStatus.TrainingPhase
                    || status.TrainingProgressCurrent
                        != lastStatus.TrainingProgressCurrent)
                {
                    Console.WriteLine("IDVB_MODEL_PROGRESS "
                        + JsonSerializer.Serialize(status));
                    lastStatus = status;
                }
                await Task.WhenAny(trainingTask, Task.Delay(500));
            }
            var result = await trainingTask;
            trainingCancellation.Cancel();
            await IgnoreCancellationAsync(parentMonitor);
            Console.WriteLine("IDVB_MODEL_PROGRESS "
                + JsonSerializer.Serialize(engine.Status));
            var output = new
            {
                schemaVersion = 1,
                manifestPath = Path.GetFullPath(manifestPath),
                repository = Path.GetFullPath(repository),
                result,
                status = engine.Status
            };
            if (outputPath is null)
                Console.WriteLine(JsonSerializer.Serialize(output,
                    new JsonSerializerOptions { WriteIndented = true }));
            else
                await RealCliOutputWriter.WriteObjectAsync(output, outputPath);
            return result.Trained ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"model-train 失败：{exception}");
            return 2;
        }
    }

    private static async Task MonitorParentAsync(int parentProcessId,
        CancellationTokenSource cancellation)
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(
                parentProcessId);
            while (!parent.HasExited)
                await Task.Delay(500, cancellation.Token);
            cancellation.Cancel();
        }
        catch (ArgumentException)
        {
            cancellation.Cancel();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
