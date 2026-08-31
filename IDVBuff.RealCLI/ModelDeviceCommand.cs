using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Diagnostics;
using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.RealCLI;

internal static class ModelDeviceCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var available = torch.cuda.is_available();
            MapLearningStatus? modelStatus = null;
            double? preprocessMilliseconds = null;
            int? preprocessVariantCount = null;
            double? warmupMilliseconds = null;
            var repositoryIndex = Array.FindIndex(args,
                value => value.Equals("--repository",
                    StringComparison.OrdinalIgnoreCase));
            if (repositoryIndex >= 0 && repositoryIndex + 1 < args.Length)
            {
                await using var engine = new MapCandidateLearningEngine(
                    args[repositoryIndex + 1]);
                await engine.InitializeAsync();
                modelStatus = engine.Status;
            }
            var imageIndex = Array.FindIndex(args,
                value => value.Equals("--preprocess-image",
                    StringComparison.OrdinalIgnoreCase));
            if (imageIndex >= 0 && imageIndex + 1 < args.Length)
            {
                using var image = Cv2.ImRead(args[imageIndex + 1],
                    ImreadModes.Unchanged);
                var stopwatch = Stopwatch.StartNew();
                if (available)
                {
                    using var inputs = MapLearningPreprocessor
                        .CreateGpuTrainingTensor(image, torch.CUDA);
                    torch.cuda.synchronize();
                    preprocessVariantCount = (int)inputs.shape[0];
                }
                else
                {
                    var inputs = MapLearningPreprocessor.CreateTrainingInputs(
                        image);
                    preprocessVariantCount = inputs.Count;
                }
                stopwatch.Stop();
                preprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
            if (available && args.Any(value => value.Equals("--warmup",
                StringComparison.OrdinalIgnoreCase)))
            {
                var stopwatch = Stopwatch.StartNew();
                using var network = new SiameseMapNetwork(torch.CUDA);
                network.EvaluationMode();
                using var noGrad = torch.no_grad();
                using var input = torch.zeros([1, 2,
                    MapLearningPreprocessor.InputSize,
                    MapLearningPreprocessor.InputSize], device: torch.CUDA);
                using var logits = network.Forward(input, input);
                _ = logits.item<float>();
                torch.cuda.synchronize();
                stopwatch.Stop();
                warmupMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
            var output = new
            {
                schemaVersion = 1,
                cudaAvailable = available,
                cudnnAvailable = available && torch.cuda.is_cudnn_available(),
                deviceCount = available ? torch.cuda.device_count() : 0,
                torchRuntime = available ? "CUDA" : "CPU",
                modelStatus,
                warmupMilliseconds,
                preprocessMilliseconds,
                preprocessVariantCount
            };
            Console.WriteLine(JsonSerializer.Serialize(output));
            return available ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 2;
        }
    }
}
