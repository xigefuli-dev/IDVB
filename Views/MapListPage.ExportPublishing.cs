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
        var updating = GetOwnedPublication() is not null;
        var publishWebsite = CreateTeachingTipChoiceButton(updating ? "更新到官网" : "发布到官网");
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(exportPackage);
        choices.Children.Add(publishWebsite);
        var tip = CreatePackageActionTeachingTip(
            publishButton,
            updating ? "更新" : "发布",
            updating ? "请选择导出普通 IDVM 地图包，或更新官网地图包。"
                : "请选择导出普通 IDVM 地图包，或生成官网签名发布目录。",
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
        var previous = GetOwnedPublication();
        var isUpdate = previous is not null;
        var packageNameText = previous?.PackageName ?? _selectedClass;
        var coverPath = previous?.CoverPath;
        var selectedScope = currentCount > 0 ? IdvmExportScope.CurrentClass : IdvmExportScope.AllClasses;
        var validationMessage = string.Empty;
        while (true)
        {
            var publisher = new TextBox { Header = "发布者账号", Text = identity.DisplayName, IsReadOnly = true };
            var scope = new ComboBox { Header = "发布地图", HorizontalAlignment = HorizontalAlignment.Stretch };
            scope.Items.Add(new ComboBoxItem
            {
                Content = $"当前地图类：{_selectedClass}（{currentCount} 张）",
                Tag = IdvmExportScope.CurrentClass,
                IsEnabled = currentCount > 0
            });
            scope.Items.Add(new ComboBoxItem { Content = $"全部地图类（{_loadedMaps.Count} 张）", Tag = IdvmExportScope.AllClasses });
            scope.SelectedIndex = selectedScope == IdvmExportScope.CurrentClass ? 0 : 1;
            scope.IsEnabled = !isUpdate;
            var packageName = new TextBox { Header = "地图包名称", Text = packageNameText, MaxLength = 80 };
            var content = new StackPanel { Spacing = 10, Width = 560 };
            content.Children.Add(publisher);
            content.Children.Add(scope);
            content.Children.Add(packageName);
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(coverPath)
                    ? "封面：自动使用主楼层预览图"
                    : $"封面：{Path.GetFileName(coverPath)}",
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(validationMessage))
                content.Children.Add(new TextBlock { Text = validationMessage, Foreground = FluentTheme.Brush("SystemFillColorCriticalBrush") });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isUpdate ? "更新 IDVB 官网地图包" : "发布到 IDVB 官网",
                Content = content,
                PrimaryButtonText = isUpdate ? "更新" : "发布",
                SecondaryButtonText = "选择封面",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            packageNameText = packageName.Text.Trim();
            if (scope.SelectedItem is ComboBoxItem { Tag: IdvmExportScope value }) selectedScope = value;
            if (result == ContentDialogResult.None) return;
            if (result == ContentDialogResult.Secondary)
            {
                await Task.Delay(250);
                coverPath = await PickPublicationCoverAsync() ?? coverPath;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(packageNameText)) break;
            validationMessage = "请填写地图包名称。";
        }
        coverPath = ResolvePublicationCoverPath(coverPath);
        await PublishMapsAsync(
            isUpdate ? IdvmExportScope.CurrentClass : selectedScope,
            identity.DisplayName,
            intendedForWebsite: true,
            packageNameText, coverPath, previous?.Id,
            previous is null ? null : MapSubscriptionLink.Parse(previous.Link).ContentKey,
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
        string packageName,
        string coverPath,
        Guid? publicationId,
        byte[]? existingContentKey,
        Button importButton,
        Button exportButton)
    {
        var publisherDisplayName = publisherText.Trim();
        var isOfficialPublisher = false;
        var isBuilderPublisher = false;
        if (intendedForWebsite)
        {
            try
            {
                var identity = await AccountSession.RequirePublishAccessAsync();
                publisherText = identity.PublisherHandle;
                publisherDisplayName = identity.DisplayName;
                isOfficialPublisher = identity.IsOfficial;
                isBuilderPublisher = identity.IsBuilder;
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
                publisherDisplayName,
                isOfficialPublisher,
                isBuilderPublisher,
                intendedForWebsite,
                packageName,
                coverPath,
                publicationId,
                existingContentKey);
            var subscriptionLink = intendedForWebsite
                ? await AccountSession.UploadPublicationAsync(publication)
                : publication.SubscriptionLink;
            if (intendedForWebsite)
            {
                var publishedMaps = scope == IdvmExportScope.CurrentClass ? GetVisibleMaps() : _loadedMaps;
                var link = MapSubscriptionLink.Parse(subscriptionLink);
                await _repository.MarkPublishedMapsAsSubscriptionAsync(
                    publishedMaps.Select(map => map.Id).ToArray(), publication.PublicationId,
                    publication.PublisherDisplayName, link.PublisherKeyId, publication.Version,
                    publication.IsOfficialPublisher, publication.IsBuilderPublisher);
                await _mapSubscriptionService.RegisterPublishedAsync(publication, subscriptionLink, publishedMaps);
                await ShowListAsync();
            }
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
                publicationId is null ? "地图发布完成" : "地图包更新完成",
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

    private MapSubscriptionRecord? GetOwnedPublication()
    {
        var maps = GetVisibleMaps();
        var ids = maps.Select(map => map.SubscriptionId).Distinct().ToArray();
        if (ids.Length != 1 || ids[0] is not { } id
            || maps.Any(map => map.AcquisitionKind != MapAcquisitionKind.Subscription)) return null;
        var identity = AccountSession.Identity;
        return identity is null ? null : _mapSubscriptionService.GetSubscriptions()
            .SingleOrDefault(record => record.Id == id
                && string.Equals(record.PublisherHandle, identity.PublisherHandle, StringComparison.OrdinalIgnoreCase));
    }

    private string GetWebsiteActionText() => GetOwnedPublication() is null ? "发布" : "更新";

    private string ResolvePublicationCoverPath(string? selectedPath)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath) && File.Exists(selectedPath)) return selectedPath;
        var map = GetVisibleMaps().OrderBy(item => item.SequenceNumber).FirstOrDefault()
            ?? throw new InvalidOperationException("当前地图类没有可用于封面的地图。");
        var floor = MapFloorRules.GetOrderedFloors(map).First();
        var preview = _repository.GetFloorThumbnailPath(map, floor.Key);
        if (!File.Exists(preview)) throw new FileNotFoundException("找不到主楼层处理后的预览图。", preview);
        return preview;
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
