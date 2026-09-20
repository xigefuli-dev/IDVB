using System.Text.Json;
using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Fusion.OpenCv;
using IDVBuff.Survey.Idvm;
using IDVBuff.Survey.Persistence.Sqlite;
using IDVBuff.Survey.PoseGraph;
using IDVBuff.Survey.Preprocessing.OpenCv;
using IDVBuff.Survey.Registration.OpenCv;
using OpenCvSharp;

namespace IDVBuff.RealCLI;

internal static class SurveyReplayCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("用法：IDVB.RealCLI survey replay --session <session.json> --out <directory>");
            return 1;
        }
        var sessionPath = ReadOption(args[1..], "--session", "-s");
        var outputPath = ReadOption(args[1..], "--out", "-o");
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            Console.Error.WriteLine("错误：--session 必须指向真实测绘会话清单。");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("错误：缺少 --out <directory>。");
            return 1;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<SurveyReplayManifest>(
                await File.ReadAllBytesAsync(sessionPath),
                JsonOptions) ?? throw new InvalidDataException("session.json 不能为空。");
            ValidateManifest(manifest, sessionPath);
            Directory.CreateDirectory(outputPath);
            return await ExecuteAsync(manifest, sessionPath, outputPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"survey replay 失败：{exception}");
            return 2;
        }
    }

    private static async Task<int> ExecuteAsync(
        SurveyReplayManifest manifest,
        string manifestPath,
        string outputPath)
    {
        var paths = new SurveyStoragePaths(Path.Combine(outputPath, "projects"));
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        var preprocessingTuning = new SurveyPreprocessingTuning();
        var registrationTuning = new SurveyRegistrationTuning();
        var fusionTuning = new SurveyFusionTuning();
        var preprocessor = new OpenCvSurveyPreprocessor(assets, preprocessingTuning);
        var registrar = new OpenCvSurveyPairRegistrar(assets, registrationTuning);
        var poseGraph = new RootPropagationPoseGraphOptimizer();
        var visualComposer = new OpenCvSurveyVisualComposer(assets, fusionTuning);
        var structureFusion = new OpenCvSurveyStructureFusion(assets, fusionTuning);
        await using var coordinator = new SurveyCoordinator(
            repository,
            assets,
            preprocessor,
            registrar,
            poseGraph,
            registrationTuning,
            visualComposer,
            structureFusion);
        var matchId = manifest.MatchId ?? Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(),
            matchId,
            manifest.OperationEpoch,
            manifest.MapClass,
            manifest.Frames[0].FloorKey,
            manifest.Name,
            "realcli-survey-default",
            OpenCvSurveyPairRegistrar.AlgorithmVersion));
        var snapshot = start.Value
            ?? throw new InvalidOperationException(start.Message ?? "无法创建测绘项目。");
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        foreach (var frame in manifest.Frames)
        {
            var framePath = Path.GetFullPath(Path.Combine(baseDirectory, frame.Path));
            var bytes = await File.ReadAllBytesAsync(framePath);
            using var image = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
            if (image.Empty())
                throw new InvalidDataException($"真实帧无法解码：{frame.Path}");
            var viewport = frame.Viewport ?? new SurveyPixelRect(0, 0, image.Width, image.Height);
            var capture = new SurveyCaptureContext(
                matchId,
                manifest.OperationEpoch,
                frame.MapToggleVersion,
                frame.CapturedAt ?? File.GetLastWriteTimeUtc(framePath),
                frame.ClientWidth > 0 ? frame.ClientWidth : image.Width,
                frame.ClientHeight > 0 ? frame.ClientHeight : image.Height,
                frame.Dpi,
                viewport,
                frame.FloorKey,
                "realcli-survey-default",
                OpenCvSurveyPairRegistrar.AlgorithmVersion);
            var result = await coordinator.AddObservationAsync(new SurveyObservationRequest(
                Guid.NewGuid(),
                snapshot.Project.ProjectId,
                snapshot.Project.Revision,
                new SurveyEncodedFrame(
                    bytes,
                    Path.GetExtension(framePath),
                    MediaType(framePath),
                    image.Width,
                    image.Height,
                    capture)));
            snapshot = result.Value?.Snapshot
                ?? throw new InvalidOperationException(result.Message ?? $"帧提交失败：{frame.Path}");
        }

        var output = await coordinator.RenderOutputsAsync(
            snapshot.Project.ProjectId,
            snapshot.Project.ActiveFloorKey);
        var dual = output.Value
            ?? throw new InvalidOperationException(output.Message ?? "无法生成测绘双结果。");
        await CopyAssetAsync(assets, snapshot.Project.ProjectId, dual.VisualMap.Asset, Path.Combine(outputPath, "visual.png"));
        await CopyAssetAsync(assets, snapshot.Project.ProjectId, dual.RecognitionStructure.Asset, Path.Combine(outputPath, "structure.png"));
        var package = new SurveyIdvmPackageService(repository, assets);
        await using (var packageStream = new FileStream(
            Path.Combine(outputPath, "project.idvm"), FileMode.Create, FileAccess.Write, FileShare.None))
            await package.ExportProjectAsync(snapshot.Project.ProjectId, packageStream);
        var resultDocument = BuildResult(snapshot, dual);
        await File.WriteAllBytesAsync(
            Path.Combine(outputPath, "result.json"),
            JsonSerializer.SerializeToUtf8Bytes(resultDocument, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(resultDocument, JsonOptions));
        return 0;
    }

    private static SurveyReplayResult BuildResult(
        SurveyProjectSnapshot snapshot,
        SurveyDualOutput output)
    {
        var layers = snapshot.Layers.ToDictionary(item => item.ObservationId);
        var observations = snapshot.Observations.Select(item =>
        {
            var transform = layers[item.ObservationId].EffectiveTransform;
            return new SurveyReplayObservationResult(
                item.ObservationId,
                item.Capture.MapToggleVersion,
                item.State.ToString(),
                item.Quality,
                item.ErrorMessage,
                transform.TranslationX,
                transform.TranslationY,
                transform.RotationDegrees,
                transform.ScaleX,
                transform.ScaleY);
        }).ToArray();
        return new SurveyReplayResult(
            snapshot.Project.ProjectId,
            snapshot.Project.Revision,
            snapshot.Observations.Count,
            snapshot.Observations.Count(item => item.State == SurveyObservationState.Registered),
            snapshot.Observations.Count(item => item.State == SurveyObservationState.Unregistered),
            snapshot.Constraints.Count,
            output.VisualMap.Asset.Sha256,
            output.RecognitionStructure.Asset.Sha256,
            observations);
    }

    private static void ValidateManifest(SurveyReplayManifest manifest, string manifestPath)
    {
        if (manifest.SchemaVersion != 1 || manifest.Frames.Count == 0)
            throw new InvalidDataException("session.json 必须是 schemaVersion=1 且至少包含一个真实帧。");
        if (manifest.OperationEpoch < 1 || string.IsNullOrWhiteSpace(manifest.MapClass))
            throw new InvalidDataException("session.json 的对局代次或地图 Class 无效。");
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var versions = new HashSet<long>();
        foreach (var frame in manifest.Frames)
        {
            if (frame.MapToggleVersion < 1
                || !versions.Add(frame.MapToggleVersion)
                || string.IsNullOrWhiteSpace(frame.FloorKey)
                || !File.Exists(Path.GetFullPath(Path.Combine(baseDirectory, frame.Path))))
                throw new InvalidDataException($"session.json 包含无效或重复的真实帧事件：{frame.Path}");
        }
    }

    private static async Task CopyAssetAsync(
        ISurveyAssetStore assets,
        Guid projectId,
        SurveyAssetReference asset,
        string destination)
    {
        await using var input = await assets.OpenReadAsync(projectId, asset);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output);
    }

    private static string? ReadOption(string[] args, params string[] names)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (names.Contains(args[index], StringComparer.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png"
    };
}
