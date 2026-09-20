// IDVB Real CLI — SessionOrchestrator 工厂（DI 容器装配）
//
// run / batch / mapopen 三个命令共享同一套真实 IDVB 服务管线装配逻辑。
// Program.cs 顶层语句的静态局部函数无法被其他文件引用，因此把 BuildOrchestrator
// 抽到此处。所有命令都只做数据搬运工——投喂截图 → 触发 IDVB → 收集结果。

using IDVBuff;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Infrastructure.Configuration;
using IDVBuff.Pipeline;
using IDVBuff.RealCLI.Stubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI.Cli;

internal static class OrchestratorFactory
{
    public static SessionOrchestrator BuildOrchestrator(
        DispatcherQueue dispatcher,
        string imagePath,
        string? settingsRoot,
        out RecordingOverlayWindow overlay)
    {
        return BuildOrchestrator(
            dispatcher,
            imagePath,
            settingsRoot,
            out overlay,
            out _);
    }

    public static SessionOrchestrator BuildOrchestrator(
        DispatcherQueue dispatcher,
        string imagePath,
        string? settingsRoot,
        out RecordingOverlayWindow overlay,
        out FileBasedCapture capture,
        string? modelRepository = null)
    {
        var services = new ServiceCollection();

        // Step 1: 注册 ConfigProvider（使用与主应用相同的数据目录）
        var configRoot = settingsRoot ?? AppDataPaths.RootDirectory;
        var configProvider = new TomlConfigProvider(configRoot);
        services.AddSingleton<IConfigProvider>(configProvider);
        services.AddSingleton<TomlConfigProvider>(configProvider);

        // Step 2: 根据截图分辨率自动匹配专属 TOML 预设
        // ⚠️ 必须在 AddIdvbServices 之前执行，确保 ApplyConfig 读取正确的预设值
        capture = new FileBasedCapture(imagePath);
        var imageBounds = capture.FullBounds;
        if (imageBounds.IsValid)
        {
            var profileManager = new ResolutionProfileManager(configProvider);
            var match = profileManager.MatchProfile(
                (int)imageBounds.Width, (int)imageBounds.Height, dpi: 120);
            if (match is not null)
            {
                configProvider.SetActivePreset(match);
            }
        }

        // Step 3: 注入真实的 IDVB 服务管线（传入已匹配分辨率的 ConfigProvider）
        services.AddIdvbServices(dispatcher, configProvider);

        // Step 4: 替换 IO 边界层为 Stub——这三个是唯一的"非真实"组件
        var recordingOverlay = new RecordingOverlayWindow();
        services.AddSingleton<IGameWindowCapture>(capture);
        services.AddSingleton<IOverlayWindow>(recordingOverlay);
        services.AddSingleton<IGlobalInput>(new NoopGlobalInput());
        services.AddSingleton<IOverlayRenderer>(new NoopOverlayRenderer());

        // Step 3.5: 覆盖 SessionOrchestrator 注册，传入 headless: true
        // AddIdvbServices 中注册的版本不带 headless 参数（默认 false），
        // 会导致 MapControlPanelWindow 创建失败（需要 WinUI 运行时）。
        // 这里用完全相同的依赖解析逻辑，仅额外传递 headless: true。
        services.AddSingleton<SessionOrchestrator>(sp =>
            new SessionOrchestrator(
                dispatcher,
                sp.GetRequiredService<ISettingsRepository>(),
                sp.GetRequiredService<IMapRepository>(),
                sp.GetRequiredService<IGameWindowCapture>(),
                sp.GetRequiredService<IOverlayWindow>(),
                sp.GetRequiredService<IGlobalInput>(),
                sp.GetRequiredService<IGateDetector>(),
                sp.GetRequiredService<IFloorRecognizer>(),
                sp.GetRequiredService<IMapIdentifier>(),
                sp.GetRequiredService<IStructureRegistrar>(),
                sp.GetRequiredService<IPlayerMarkerDetector>(),
                sp.GetRequiredService<IConfigProvider>(),
                sp.GetRequiredService<IResolutionProfileService>(),
                sp.GetRequiredService<PipelineFactory>(),
                sp.GetRequiredService<MapAlignmentResearchCollector>(),
                sp.GetRequiredService<IDVBuff.Survey.Contracts.ISurveyCoordinator>(),
                sp.GetRequiredService<IDVBuff.Survey.Contracts.SurveyCaptureTuning>(),
                headless: true,
                learningEngine: new MapCandidateLearningEngine(modelRepository)));
        services.AddSingleton<ISessionOrchestrator>(sp =>
            sp.GetRequiredService<SessionOrchestrator>());

        // Step 5: 构建并返回真实 SessionOrchestrator
        var serviceProvider = services.BuildServiceProvider();
        overlay = recordingOverlay;
        return serviceProvider.GetRequiredService<SessionOrchestrator>();
    }
}
