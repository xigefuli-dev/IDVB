using IDVBuff.Features.Maps;
using IDVBuff.Helps;
using IDVBuff.Modules;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private const string MapSubscriptionGuideCode = "IDVB-SUBSCRIBE-MAP-GUIDE";

    private async Task ShowMapSubscriptionGuideAsync()
    {
        NavigateTo("map-list");
        await Task.Delay(50);
        if (ModuleContentHost.Content is not MapListPage mapListPage)
            return;

        var guide = new EmphasisGuide(EmphasisGuideHost);
        await guide.ShowAsync(
        [
            new EmphasisGuideStep(
                "前往地图社区",
                "点击下方链接。IDVB 会复制一个引导口令，地图社区识别后会继续带你选择地图包。",
                NextButtonDelay: TimeSpan.Zero,
                ActionButtonText: "打开 https://community.idvb.xgflee.com/",
                ActionAsync: OpenMapCommunityGuideAsync)
        ]);

        var subscriptionLink = await WaitForSubscriptionLinkAsync();
        await guide.ShowAsync(
        [
            new EmphasisGuideStep(
                "打开地图订阅",
                "已识别订阅链接。点击“导入”，再点击“地图订阅”。",
                mapListPage.GetImportControl(),
                NextButtonDelay: TimeSpan.Zero)
        ]);
        await mapListPage.ShowMapSubscriptionsTutorialAsync(subscriptionLink);
        await guide.ShowAsync(
        [
            new EmphasisGuideStep("订阅地图完成", "地图订阅流程已经结束。点击“完成”关闭教程。", NextButtonDelay: TimeSpan.Zero, AdvanceButtonText: "完成")
        ]);
    }

    private static async Task OpenMapCommunityGuideAsync(CancellationToken cancellationToken)
    {
        var package = new DataPackage();
        package.SetText(MapSubscriptionGuideCode);
        Clipboard.SetContent(package);
        await Launcher.LaunchUriAsync(new Uri("https://community.idvb.xgflee.com/?guide=subscribe"));
    }

    private static async Task<string> WaitForSubscriptionLinkAsync()
    {
        while (true)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (text.StartsWith("idvb-sub:", StringComparison.OrdinalIgnoreCase))
                        return text;
                }
            }
            catch { }
            await Task.Delay(400);
        }
    }
    /// <summary>Runs after the user accepts the first-run recommended configuration.</summary>
    public async Task ShowRecommendedConfigurationGuideAsync()
    {
        _onboardingNavigationLocked = true;
        SetNavigationCompact(false);
        foreach (var entry in NavigationItems)
            ExpandOnboardingNavigation(entry);

        try
        {
            NavigateTo("map-status", NavigationItems.FirstOrDefault(entry => entry.ModuleId == "map-status"));
            await Task.Delay(50);
            if (ModuleContentHost.Content is not MapStatusPage statusPage)
                return;

            var guide = new EmphasisGuide(EmphasisGuideHost);
            await guide.ShowAsync(
            [
                CreateBindingStep("绑定游戏地图开关", "请先检查你游戏内打开地图的按键是哪个，然后在这里绑定一样的按键。千万不要按 ESC 或者用鼠标关闭地图，必须通过这个按键来关闭地图。", statusPage, MapRuntimeBindingTarget.GameMapToggle, "游戏地图开关", "game-map-toggle.png"),
                CreateBindingStep("绑定外置控件层", "在进入战局后，按下此按键打开控件菜单。请在选择好本局使用的地图后，点击进入对局。\n\n在结束对局后，同样需要按下此按键打开控件菜单，点击结束对局。", statusPage, MapRuntimeBindingTarget.ControlPanelToggle, "外置控件层", "control-panel-start.png", "control-panel-end.png"),
                new EmphasisGuideStep("绑定快捷扫描", string.Empty, statusPage.GetBindingControl(MapRuntimeBindingTarget.QuickScan), CheckAsync: _ => RequireBindingAsync(MapRuntimeBindingTarget.QuickScan, "快捷扫描"), DescriptionSegmentsFactory: CreateQuickScanGuideDescription, ImageUris: GuideImages("quick-scan-start.png", "quick-scan-complete.png", "quick-scan-select-map.png", "quick-scan-map-open.png")),
            ]);
            await ShowMapImportAndActivationGuideAsync();
        }
        finally
        {
            _onboardingNavigationLocked = false;
        }
    }

    private void ExpandOnboardingNavigation(NavigationEntry entry)
    {
        if (entry.Children.Count > 0 && !entry.Node.IsExpanded)
        {
            entry.Node.ToggleExpanded();
            AnimateNavigationExpansion(entry);
        }

        foreach (var child in entry.Children)
            ExpandOnboardingNavigation(child);
    }

    private async Task ShowMapImportAndActivationGuideAsync()
    {
        var repository = new MapRepository();
        var mapCountBeforeImport = (await repository.GetMapsAsync()).Count;
        NavigateTo("map-list");
        await Task.Delay(50);
        if (ModuleContentHost.Content is not MapListPage mapListPage)
            return;

        var importGuide = new EmphasisGuide(EmphasisGuideHost);
        await importGuide.ShowAsync(
        [
            new EmphasisGuideStep(
                "导入地图包",
                "点击“导入”，选择“导入数据包”，然后选择你自己的 IDVM 地图包。导入完成后点击检查。",
                CheckAsync: _ => RequireImportedMapAsync(repository, mapCountBeforeImport),
                TargetProvider: mapListPage.GetImportControl)
        ]);

        NavigateTo("map-status");
        await Task.Delay(50);
        if (ModuleContentHost.Content is not MapStatusPage statusPage)
            return;

        var activationGuide = new EmphasisGuide(EmphasisGuideHost);
        await activationGuide.ShowAsync(
        [
            new EmphasisGuideStep(
                "打开总开关",
                "地图包已导入。回到“加页手记 → 配置”后，在这里打开总开关。",
                statusPage.GetRuntimeEnableControl(),
                CheckAsync: _ => RequireRuntimeEnabledAsync())
        ]);

        await ShowPreMatchVideoGuidesAsync(statusPage);
    }

    private async Task ShowPreMatchVideoGuidesAsync(MapStatusPage statusPage)
    {
        var calibrationVideoOpened = false;
        var startMatchVideoOpened = false;
        var inGameVideoOpened = false;
        var endMatchVideoOpened = false;
        var guide = new EmphasisGuide(EmphasisGuideHost);
        await guide.ShowAsync(
        [
            new EmphasisGuideStep(
                "校准显示区域",
                "请点击“校准地图区域”，完成一次完整地图画布的框选；如果已经校准并保存，可以直接继续。点击“观看视频教程”后，再点击检查。",
                statusPage.GetMapViewportCalibrationControl(),
                CheckAsync: _ => RequireCalibrationAndVideoAsync(
                    calibrationVideoOpened),
                EnterAsync: _ => BringCalibrationControlIntoViewAsync(statusPage),
                TutorialVideoUri: TutorialVideoUri("vid1.mp4"),
                VideoOpened: () => calibrationVideoOpened = true),
            new EmphasisGuideStep(
                "如何开始对局",
                "观看教程，了解如何打开外置控件层、选择本局地图并开始对局。",
                CheckAsync: _ => RequireVideoOpenedAsync(startMatchVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid2.mp4"),
                VideoOpened: () => startMatchVideoOpened = true),
            new EmphasisGuideStep(
                "游戏实机操作",
                "观看教程，了解进入游戏后打开地图、快捷扫描和选择对应地图的操作。",
                CheckAsync: _ => RequireVideoOpenedAsync(inGameVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid3.mp4"),
                VideoOpened: () => inGameVideoOpened = true),
            new EmphasisGuideStep(
                "结束游戏",
                "观看教程，了解结束对局后如何通过外置控件层结束本局。",
                CheckAsync: _ => RequireVideoOpenedAsync(endMatchVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid4.mp4"),
                VideoOpened: () => endMatchVideoOpened = true),
            new EmphasisGuideStep(
                "进入游戏体验",
                "准备完成。请进入游戏，使用刚才绑定的按键开始体验 Identity Vision Bridge。",
                NextButtonDelay: TimeSpan.Zero),
            new EmphasisGuideStep(
                "了解更多能力",
                "如果需要了解其他能力的介绍和使用方法，可以前往左侧导航栏的“教程”查找。",
                TargetProvider: () => TryGetNavigationRow(TutorialNavigationItem),
                NextButtonDelay: TimeSpan.Zero,
                AdvanceButtonText: "完成")
        ]);
    }

    private static EmphasisGuideStep CreateBindingStep(string title, string description, MapStatusPage page, MapRuntimeBindingTarget target, string displayName, params string[] imageFiles) =>
        new(title, description, page.GetBindingControl(target), CheckAsync: _ => RequireBindingAsync(target, displayName), ImageUris: GuideImages(imageFiles));

    private static IReadOnlyList<string> GuideImages(params string[] imageFiles) =>
        imageFiles.Select(file => $"ms-appx:///Assets/Guide/{file}").ToArray();

    private static Task<EmphasisGuideCheckResult> RequireBindingAsync(MapRuntimeBindingTarget target, string displayName)
    {
        var binding = target switch
        {
            MapRuntimeBindingTarget.GameMapToggle => App.Session.Settings.GameMapToggleBinding,
            MapRuntimeBindingTarget.ControlPanelToggle => App.Session.Settings.ControlPanelToggleBinding,
            MapRuntimeBindingTarget.QuickScan => App.Session.Settings.QuickScanBinding,
            MapRuntimeBindingTarget.SwitchFloor => App.Session.Settings.SwitchFloorBinding,
            MapRuntimeBindingTarget.SaveMapCache => App.Session.Settings.SaveMapCacheBinding,
            MapRuntimeBindingTarget.RestMapDisplay => App.Session.Settings.RestMapDisplayBinding,
            MapRuntimeBindingTarget.MatchStateToggle => App.Session.Settings.MatchStateToggleBinding,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        return Task.FromResult(binding.IsConfigured ? EmphasisGuideCheckResult.Passed : EmphasisGuideCheckResult.TryAgain($"请先为“{displayName}”设置按键。"));
    }

    private static async Task<EmphasisGuideCheckResult> RequireImportedMapAsync(
        MapRepository repository,
        int mapCountBeforeImport)
    {
        var mapCount = (await repository.GetMapsAsync()).Count;
        return mapCount > mapCountBeforeImport
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("尚未检测到新导入的地图包，请完成 IDVM 数据包导入后再检查。");
    }

    private static Task<EmphasisGuideCheckResult> RequireRuntimeEnabledAsync() =>
        Task.FromResult(App.Session.Settings.IsEnabled
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("请先打开总开关。"));

    private static string TutorialVideoUri(string fileName) =>
        $"https://download.xgflee.com/guides/onboarding/{fileName}";

    private static Task<EmphasisGuideCheckResult> RequireVideoOpenedAsync(bool wasOpened) =>
        Task.FromResult(wasOpened
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("请先点击“观看视频教程”并打开教程，再点击检查。"));

    private static Task<EmphasisGuideCheckResult> RequireCalibrationAndVideoAsync(
        bool videoWasOpened)
    {
        if (!videoWasOpened)
            return Task.FromResult(EmphasisGuideCheckResult.TryAgain("请先点击“观看视频教程”并打开教程。"));

        return Task.FromResult(App.Session.Settings.IsMapViewportCalibrated
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("请先完成并保存“校准地图区域”，再点击检查。"));
    }

    private static async Task BringCalibrationControlIntoViewAsync(MapStatusPage page)
    {
        page.GetMapViewportCalibrationControl()?.StartBringIntoView();
        await Task.Delay(50);
    }

    private static IReadOnlyList<EmphasisGuideTextSegment> CreateQuickScanGuideDescription()
    {
        var gameMapKey = App.Session.Settings.GameMapToggleBinding.DisplayName;
        return [new("在打开游戏内地图后，按下此按键即可开始扫描地图。扫描工作开始后，请用“游戏地图开关”对应的按键（也就是“"), new(gameMapKey, true), new("”）关闭地图。扫描期间不要打开地图，等待扫描完毕后，按下“游戏地图开关”对应的按键（也就是“"), new(gameMapKey, true), new("”），选择这局对应的地图。")];
    }
}
