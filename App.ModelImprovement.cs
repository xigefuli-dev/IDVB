using IDVBuff.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff;

public partial class App
{
    public static Features.Maps.SessionOrchestrator? CurrentSession =>
        _currentApp?._serviceProvider?.GetService<Features.Maps.SessionOrchestrator>();

    private async Task InitializeModelImprovementAsync(
        MainProgramPreferences preferences,
        bool startMinimized)
    {
        await ShowModelImprovementConsentIfNeededAsync(preferences, startMinimized);
        if (preferences.HelpImproveModels)
            _ = ModelImprovementUploadService.TryUploadDailyAsync(preferences);
    }

    private async Task ShowModelImprovementConsentIfNeededAsync(
        MainProgramPreferences preferences,
        bool startMinimized)
    {
        if (preferences.ModelImprovementConsentPromptCompleted)
            return;

        if (startMinimized)
            ShowMainWindow();
        var xamlRoot = await WaitForMainXamlRootAsync();
        if (xamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "愿意帮助我们把模型做得更好吗？",
            Content = new TextBlock
            {
                Text = "开启后，你在正常使用 IDVB 时产生的有效反馈，可以帮助我们改进地图识别与对齐效果，让后续版本更准确、更稳定。\n\n"
                    + "我们只会收集并上传与地图识别、对齐和模型训练相关的脱敏数据，不会上传无关的屏幕内容、个人文件、账号信息或普通日志。脱敏训练包每天最多上传一次。\n\n"
                    + "你可以现在选择，也可以稍后在“主设置 - 隐私 - 帮助我们改进模型”中打开或关闭。",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                FontSize = 14
            },
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560d;
        var enabled = await dialog.ShowAsync() == ContentDialogResult.Primary;

        await ModelImprovementPreferences.ApplyDataCollectionAsync(enabled);
        preferences.HelpImproveModels = enabled;
        preferences.ModelImprovementConsentPromptCompleted = true;
        preferences.Save();
    }
}
