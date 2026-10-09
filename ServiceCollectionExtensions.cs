// IDVB Remaster — DI Composition Root

using IDVBuff.Diagnostics;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.Adapters;
using IDVBuff.Features.Capture;
using IDVBuff.Features.Plugins;
using IDVBuff.Infrastructure.Configuration;
using IDVBuff.Pipeline;
using IDVBuff.Pipeline.Stages;
using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Persistence.Sqlite;
using IDVBuff.Survey.PoseGraph;
using IDVBuff.Survey.Preprocessing.OpenCv;
using IDVBuff.Survey.Registration.OpenCv;
using IDVBuff.Survey.Fusion.OpenCv;
using IDVBuff.Survey.Idvm;
using IDVBuff.PluginContracts;
using IDVBuff.Features.Notifications;
using IdentityVisionBridge.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;

namespace IDVBuff;

/// <summary>
/// 集中注册所有 IDVB 服务的扩展方法。由 App.xaml.cs 在启动时调用。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdvbServices(
        this IServiceCollection services,
        DispatcherQueue dispatcher,
        IConfigProvider? configProvider = null,
        bool headless = false)
    {
        // ════════════════════════════════════════════════════════════
        // Infrastructure — Configuration
        // ════════════════════════════════════════════════════════════
        using var registrationTiming = StartupTimeline.Measure("DI registration body (after method JIT)");
        using (StartupTimeline.Measure("DI configuration provider construction"))
            configProvider ??= new TomlConfigProvider(AppDataPaths.RootDirectory);
        services.AddSingleton(configProvider);
        if (configProvider is TomlConfigProvider toml)
            services.AddSingleton(toml);
        services.AddSingleton<IResolutionProfileService>(sp =>
            new ResolutionProfileManager(
                sp.GetRequiredService<IConfigProvider>() is TomlConfigProvider t
                    ? t
                    : new TomlConfigProvider(AppDataPaths.RootDirectory)));

        // 将 TOML 配置应用到各算法模块的静态规则类
        using (StartupTimeline.Measure("DI GateTemplateRules.ApplyConfig"))
            GateTemplateRules.ApplyConfig(configProvider);
        using (StartupTimeline.Measure("DI RecognitionConfigRules.ApplyConfig"))
            RecognitionConfigRules.ApplyConfig(configProvider);
        using (StartupTimeline.Measure("DI StructureRegistrationRules.ApplyConfig"))
            StructureRegistrationRules.ApplyConfig(configProvider);
        using (StartupTimeline.Measure("DI SideEntranceScanRules.ApplyConfig"))
            SideEntranceScanRules.ApplyConfig(configProvider);
        using (StartupTimeline.Measure("DI OverlayDisplayRules.ApplyConfig"))
            OverlayDisplayRules.ApplyConfig(configProvider);

        // ════════════════════════════════════════════════════════════
        // Data — Repository
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IMapRepository, MapRepositoryAdapter>();
        services.AddSingleton<ISettingsRepository, SettingsRepositoryAdapter>();
        services.AddSingleton(_ => new SurveyStoragePaths(
            Path.Combine(AppDataPaths.RootDirectory, "SurveyProjects")));
        services.AddSingleton<ISurveyProjectRepository, SqliteSurveyProjectRepository>();
        services.AddSingleton<ISurveyAssetStore, ContentAddressedSurveyAssetStore>();
        services.AddSingleton<ISurveyTemplateStore>(_ => new JsonSurveyTemplateStore(
            Path.Combine(
                AppDataPaths.RootDirectory,
                "Survey",
                JsonSurveyTemplateStore.DefaultFileName)));
        services.AddSingleton(sp =>
        {
            var cp = sp.GetRequiredService<IConfigProvider>();
            var cfg = cp.Get<SurveyCaptureTuning>("survey.capture");
            cfg.Validate();
            return cfg;
        });
        services.AddSingleton(sp =>
        {
            var cp = sp.GetRequiredService<IConfigProvider>();
            var cfg = cp.Get<SurveyPreprocessingTuning>("survey.preprocessing");
            cfg.Validate();
            return cfg;
        });
        services.AddSingleton(sp =>
        {
            var cp = sp.GetRequiredService<IConfigProvider>();
            var cfg = cp.Get<SurveyRegistrationTuning>("survey.registration");
            cfg.Validate();
            return cfg;
        });
        services.AddSingleton(sp =>
        {
            var cp = sp.GetRequiredService<IConfigProvider>();
            var cfg = cp.Get<SurveyStorageTuning>("survey.storage");
            cfg.Validate();
            return cfg;
        });
        services.AddSingleton(sp =>
        {
            var cp = sp.GetRequiredService<IConfigProvider>();
            var cfg = cp.Get<SurveyFusionTuning>("survey.fusion.visual");
            var structure = cp.Get<SurveyFusionTuning>("survey.fusion.structure");
            cfg.StructureBinaryThreshold = structure.StructureBinaryThreshold;
            cfg.Validate();
            return cfg;
        });
        services.AddSingleton<ISurveyPreprocessor, OpenCvSurveyPreprocessor>();
        services.AddSingleton<ISurveyLayerRasterEditor, OpenCvSurveyLayerRasterEditor>();
        services.AddSingleton<ISurveyPairRegistrar, OpenCvSurveyPairRegistrar>();
        services.AddSingleton<IPoseGraphOptimizer, RootPropagationPoseGraphOptimizer>();
        services.AddSingleton<ISurveyVisualComposer, OpenCvSurveyVisualComposer>();
        services.AddSingleton<ISurveyStructureFusion, OpenCvSurveyStructureFusion>();
        services.AddSingleton<ISurveyPackageService, SurveyIdvmPackageService>();
        services.AddSingleton<ISurveyCoordinator, SurveyCoordinator>();

        // ════════════════════════════════════════════════════════════
        // Infrastructure — Capture & Input
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IGameWindowCapture, GameWindowCaptureAdapter>();
        services.AddSingleton<IPluginScreenshotService, PluginScreenshotService>();
        services.AddSingleton<IPluginGameWindowService, PluginGameWindowService>();
        // 游戏内浮层提示通道（插件用它给「已进入对局 / 已结束对局」这类动作一个可见反馈）。
        services.AddSingleton<IGameOverlayToast, GameOverlayToast>();
        services.AddSingleton<IGlobalInput>(_ =>
            new GlobalInputAdapter(dispatcher));

        // ════════════════════════════════════════════════════════════
        // Services — Detection
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IGateDetector>(_ =>
            new GateDetectorAdapter(MapCvRecognitionHelpers.ResolveGatePath()));
        services.AddSingleton<IFloorRecognizer>(_ =>
        {
            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            return new FloorRecognizerAdapter(
                Path.Combine(assetsDir, "1F.png"),
                Path.Combine(assetsDir, "2F.png"));
        });
        services.AddSingleton<IPlayerMarkerDetector, PlayerMarkerDetectorAdapter>();

        // ════════════════════════════════════════════════════════════
        // Services — Recognition
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IMapIdentifier, MapIdentifierAdapter>();
        services.AddSingleton<MapStructurePreprocessor>();
        services.AddSingleton<MapStructureFiller>();
        services.AddSingleton<IStructureRegistrar>(sp =>
            new StructureRegistrarAdapter(sp.GetRequiredService<MapStructurePreprocessor>()));

        // ════════════════════════════════════════════════════════════
        // Services — Overlay
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<ICaptureProtectionService, WindowCaptureProtectionService>();
        services.AddSingleton<IOverlayWindow, OverlayWindowAdapter>();
        services.AddSingleton<IOverlayRenderer, OverlayRendererAdapter>();
        services.AddSingleton<OverlayNotificationQueue>();
        services.AddSingleton<IOverlayNotificationService>(sp =>
        {
            var queue = sp.GetRequiredService<OverlayNotificationQueue>();
            var capture = sp.GetService<ICaptureProtectionService>();
            var service = new OverlayNotificationService(queue, capture);
            OverlayNotificationCenter.Initialize(service);
            return service;
        });

        // ════════════════════════════════════════════════════════════
        // Services — Logging & Research
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<MapLogCollector>();
        services.AddSingleton<MapAlignmentResearchCollector>(sp =>
            new MapAlignmentResearchCollector(
                sp.GetRequiredService<MapStructurePreprocessor>()));

        // ════════════════════════════════════════════════════════════
        // Pipeline Stages
        // ════════════════════════════════════════════════════════════
        services.AddTransient<CaptureStage>();
        services.AddTransient<FloorDetectStage>();
        services.AddTransient<GateDetectStage>();
        services.AddTransient<MapIdentifyStage>();
        services.AddTransient<StrategySelectStage>();
        services.AddTransient<TransformCalcStage>();
        services.AddTransient<RefineStage>();
        services.AddTransient<ValidateStage>();
        services.AddTransient<ProjectStage>();

        // PipelineFactory
        services.AddSingleton<PipelineFactory>();

        // ════════════════════════════════════════════════════════════
        // Session Orchestrator（新架构唯一入口）
        // 双重注册：具体类型供 Views 使用，接口供管线层使用
        // ════════════════════════════════════════════════════════════
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
                sp.GetRequiredService<ISurveyCoordinator>(),
                sp.GetRequiredService<SurveyCaptureTuning>(),
                sp.GetRequiredService<ICaptureProtectionService>(),
                headless: headless));
        services.AddSingleton<ISessionOrchestrator>(sp =>
            sp.GetRequiredService<SessionOrchestrator>());
        // 插件对局控制能力：内置插件与第三方插件共用同一套语义
        // （实现见 Features\Maps\SessionOrchestrator.HostMatchCapability.cs，只做转发）。
        services.AddSingleton<IHostMatchCapability>(sp =>
            sp.GetRequiredService<SessionOrchestrator>());

        return services;
    }
}
