using IDVBuff.Features.Maps;
using IDVBuff.Features.Accounts;
using IDVBuff.UpdateCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private TeachingTip CreatePublishTeachingTip(
        Button importButton,
        Button publishButton)
    {
        var exportPackage = CreateTeachingTipChoiceButton("导出地图包");
        var publishWebsite = CreateTeachingTipChoiceButton("发布到官网");
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(exportPackage);
        choices.Children.Add(publishWebsite);
        var tip = CreatePackageActionTeachingTip(
            publishButton,
            "发布",
            "请选择导出普通 IDVM 地图包，或生成官网签名发布目录。",
            choices);

        exportPackage.Click += async (_, _) =>
        {
            await CloseTeachingTipAsync(tip);
            await ShowExportIdvmDialogAsync(importButton, publishButton);
        };
        publishWebsite.Click += async (_, _) =>
        {
            await CloseTeachingTipAsync(tip);
            await ShowWebsitePublishDialogAsync(importButton, publishButton);
        };
        return tip;
    }

    private async Task ShowExportIdvmDialogAsync(
        Button importButton,
        Button publishButton)
    {
        var currentCount = GetVisibleMaps().Count;
        var totalCount = _loadedMaps.Count;
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(new TextBlock
        {
            Text = $"当前地图类：{_selectedClass}（{currentCount} 张地图）",
            TextWrapping = TextWrapping.Wrap
        });
        choices.Children.Add(new TextBlock
        {
            Text = $"全部地图类：{totalCount} 张地图",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择导出范围",
            Content = choices,
            PrimaryButtonText = "当前地图类",
            SecondaryButtonText = "全部地图类",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = currentCount > 0,
            IsSecondaryButtonEnabled = totalCount > 0,
            DefaultButton = currentCount > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Secondary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return;
        await ExportIdvmAsync(
            result == ContentDialogResult.Primary
                ? IdvmExportScope.CurrentClass
                : IdvmExportScope.AllClasses,
            importButton,
            publishButton);
    }

    private async Task ShowWebsitePublishDialogAsync(
        Button importButton,
        Button publishButton)
    {
        AccountIdentity identity;
        try
        {
            identity = await AccountSession.RequirePublishAccessAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("需要登录", exception.Message);
            return;
        }
        var currentCount = GetVisibleMaps().Count;
        var publisher = new TextBox
        {
            Header = "发布者账号",
            Text = identity.DisplayName,
            IsReadOnly = true
        };
        var scope = new ComboBox
        {
            Header = "发布地图",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scope.Items.Add(new ComboBoxItem
        {
            Content = $"当前地图类：{_selectedClass}（{currentCount} 张）",
            Tag = IdvmExportScope.CurrentClass,
            IsEnabled = currentCount > 0
        });
        scope.Items.Add(new ComboBoxItem
        {
            Content = $"全部地图类（{_loadedMaps.Count} 张）",
            Tag = IdvmExportScope.AllClasses
        });
        scope.SelectedIndex = currentCount > 0 ? 0 : 1;
        var content = new StackPanel { Spacing = 10, Width = 560 };
        content.Children.Add(publisher);
        content.Children.Add(scope);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "发布到 IDVB 官网",
            Content = content,
            PrimaryButtonText = "发布",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || scope.SelectedItem is not ComboBoxItem { Tag: IdvmExportScope selectedScope })
            return;
        await PublishMapsAsync(
            selectedScope,
            publisher.Text,
            intendedForWebsite: true,
            importButton,
            publishButton);
    }

    private static async Task CloseTeachingTipAsync(TeachingTip tip)
    {
        if (!tip.IsOpen)
            return;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            sender.Closed -= Complete;
            completion.TrySetResult(true);
        }
        tip.Closed += Complete;
        tip.IsOpen = false;
        _ = await Task.WhenAny(completion.Task, Task.Delay(600));
        tip.Closed -= Complete;
    }

    private static Button CreateTeachingTipChoiceButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        MinWidth = 150
    };

    private async Task ExportIdvmAsync(
        IdvmExportScope scope,
        Button importButton,
        Button exportButton)
    {
        var suggestedName = scope == IdvmExportScope.CurrentClass
            ? $"IDVB-{SanitizeFileName(_selectedClass)}-{DateTime.Now:yyyyMMdd-HHmmss}"
            : $"IDVB-All-{DateTime.Now:yyyyMMdd-HHmmss}";
        var destination = await PickIdvmDestinationAsync(suggestedName);
        if (destination is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导出…");
        try
        {
            await Task.Run(() => _idvmPackageService.ExportAsync(
                scope,
                scope == IdvmExportScope.CurrentClass ? _selectedClass : null,
                destination));
            await ShowMessageAsync("数据包导出完成", $"已保存到：\n{destination}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("数据包导出失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private async Task PublishMapsAsync(
        IdvmExportScope scope,
        string publisherText,
        bool intendedForWebsite,
        Button importButton,
        Button exportButton)
    {
        if (intendedForWebsite)
        {
            try
            {
                var identity = await AccountSession.RequirePublishAccessAsync();
                publisherText = identity.PublisherHandle;
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("需要登录", exception.Message);
                return;
            }
        }
        string publisherHandle;
        try
        {
            publisherHandle = MapSubscriptionProtocol.NormalizePublisherHandle(publisherText);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("发布者账号无效", exception.Message);
            return;
        }

        var outputDirectory = intendedForWebsite
            ? Path.Combine(AppDataPaths.RootDirectory, "MapPublishing", "WebsiteOutbox")
            : await PickPublicationFolderAsync();
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在发布…");
        try
        {
            var publication = await _mapPublicationService.PublishAsync(
                scope,
                scope == IdvmExportScope.CurrentClass ? _selectedClass : null,
                outputDirectory,
                publisherHandle,
                intendedForWebsite);
            var subscriptionLink = intendedForWebsite
                ? await AccountSession.UploadPublicationAsync(publication)
                : publication.SubscriptionLink;
            var data = new DataPackage();
            data.SetText(subscriptionLink);
            Clipboard.SetContent(data);
            var publisherName = intendedForWebsite
                ? AccountSession.Identity?.DisplayName ?? publication.PublisherHandle
                : publication.PublisherHandle;
            var targetText = intendedForWebsite
                ? "已发布到 IDVB 官网"
                : "已生成本地更新订阅";
            await ShowMessageAsync(
                "地图发布完成",
                $"{targetText}\n发布者：{publisherName}\n订阅链接已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("地图发布失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private async Task<string?> PickIdvmDestinationAsync(string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".idvm",
                CommitButtonText = "导出",
                FileTypeChoices =
                {
                    { "IDVM 地图数据包", new List<string> { ".idvm" } }
                }
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
                return null;
            return Path.GetExtension(result.Path).Equals(".idvm", StringComparison.OrdinalIgnoreCase)
                ? result.Path
                : result.Path + ".idvm";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开保存选择器", exception.Message);
            return null;
        }
    }
}
