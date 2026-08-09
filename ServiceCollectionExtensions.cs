// IDVB Remaster — DI Composition Root

using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.Adapters;
using IDVBuff.Infrastructure.Configuration;
using IDVBuff.Pipeline;
using IDVBuff.Pipeline.Stages;
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
        configProvider ??= new TomlConfigProvider();
        services.AddSingleton(configProvider);
        if (configProvider is TomlConfigProvider toml)
            services.AddSingleton(toml);
        services.AddSingleton<IResolutionProfileService>(sp =>
            new ResolutionProfileManager(
                sp.GetRequiredService<IConfigProvider>() is TomlConfigProvider t
                    ? t
                    : new TomlConfigProvider()));

        // 将 TOML 配置应用到各算法模块的静态规则类
        GateTemplateRules.ApplyConfig(configProvider);
        RecognitionConfigRules.ApplyConfig(configProvider);
        StructureRegistrationRules.ApplyConfig(configProvider);
        SideEntranceScanRules.ApplyConfig(configProvider);
        OverlayDisplayRules.ApplyConfig(configProvider);

        // ════════════════════════════════════════════════════════════
        // Data — Repository
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IMapRepository, MapRepositoryAdapter>();
        services.AddSingleton<ISettingsRepository, SettingsRepositoryAdapter>();

        // ════════════════════════════════════════════════════════════
        // Infrastructure — Capture & Input
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IGameWindowCapture, GameWindowCaptureAdapter>();
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
        services.AddSingleton<IStructureRegistrar>(sp =>
            new StructureRegistrarAdapter(sp.GetRequiredService<MapStructurePreprocessor>()));

        // ════════════════════════════════════════════════════════════
        // Services — Overlay
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<IOverlayWindow, OverlayWindowAdapter>();
        services.AddSingleton<IOverlayRenderer, OverlayRendererAdapter>();

        // ════════════════════════════════════════════════════════════
        // Services — Logging & Research
        // ════════════════════════════════════════════════════════════
        services.AddSingleton<MapLogCollector>();
        services.AddSingleton<MapAlignmentResearchCollector>();

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
                headless: headless));
        services.AddSingleton<ISessionOrchestrator>(sp =>
            sp.GetRequiredService<SessionOrchestrator>());

        return services;
    }
}
