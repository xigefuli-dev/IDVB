using IDVBuff.UpdateCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    public async Task ShowMapSubscriptionsTutorialAsync(string subscriptionLink)
    {
        if (_mapSubscriptionService.GetSubscriptions().Any(record =>
                string.Equals(record.Link, subscriptionLink, StringComparison.Ordinal)))
            return;
        await ShowMapSubscriptionsDialogAsync(_importButton!, _publishButton!, showTutorial: true);
    }

    private async Task ShowMapSubscriptionsDialogAsync(Button importButton, Button exportButton, bool showTutorial = false)
    {
        var reconciliation = await _mapSubscriptionService.ReconcileInstalledMapsAsync();
        var content = new StackPanel { Spacing = 10, MinWidth = 560 };
        var linkBox = new TextBox
        {
            PlaceholderText = "粘贴地图订阅链接",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (showTutorial)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "粘贴订阅链接",
                Message = "粘贴地图社区刚刚复制的订阅链接，点击“添加并更新”，完成后点击“完成”。"
            });
        }
        content.Children.Add(linkBox);
        var status = new TextBlock
        {
            Text = reconciliation.RemovedSubscriptionCount > 0
                ? $"已清理 {reconciliation.RemovedSubscriptionCount} 个无效订阅。"
                : reconciliation.IncompleteSubscriptionCount > 0
                    ? $"有 {reconciliation.IncompleteSubscriptionCount} 个订阅需要更新。"
                    : string.Empty,
            TextWrapping = TextWrapping.Wrap
        };
        var recordsHost = new StackPanel { Spacing = 6 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addButton = new Button { Content = "添加并更新" };
        var updateButton = new Button { Content = "立即更新全部" };
        var progressRing = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var isUpdating = false;
        var refreshListAfterDialog = false;
        var updateStartedAt = DateTimeOffset.MinValue;
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        progressTimer.Tick += (_, _) =>
        {
            if (isUpdating)
                status.Text = $"正在检查并应用订阅更新……已等待 {(int)(DateTimeOffset.Now - updateStartedAt).TotalSeconds} 秒，请保持此窗口打开。";
        };
        actions.Children.Add(addButton);
        actions.Children.Add(updateButton);
        actions.Children.Add(progressRing);
        content.Children.Add(actions);
        content.Children.Add(status);
        content.Children.Add(new ScrollViewer
        {
            Content = recordsHost,
            MaxHeight = 320,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        void RenderRecords()
        {
            recordsHost.Children.Clear();
            var records = _mapSubscriptionService.GetSubscriptions();
            if (records.Count == 0)
            {
                recordsHost.Children.Add(new TextBlock { Text = "尚未添加更新订阅。", Opacity = .7 });
                return;
            }
            foreach (var record in records)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var toggle = new ToggleSwitch { IsOn = record.Enabled, VerticalAlignment = VerticalAlignment.Center };
                toggle.IsEnabled = !isUpdating;
                toggle.Toggled += async (_, _) =>
                {
                    try { await _mapSubscriptionService.SetEnabledAsync(record.Id, toggle.IsOn); }
                    catch (Exception exception) { status.Text = "修改订阅失败：" + exception.Message; }
                };
                row.Children.Add(toggle);
                var publisherName = string.IsNullOrWhiteSpace(record.PublisherDisplayName)
                    ? "等待首次更新"
                    : record.PublisherDisplayName.TrimStart('@');
                var official = record.IsOfficialPublisher ? " · 官方" : string.Empty;
                var packageName = string.IsNullOrWhiteSpace(record.PackageName)
                    ? string.Empty
                    : $"\n地图包：{record.PackageName}";
                var details = new TextBlock
                {
                    Text = $"{publisherName}{official}{packageName}"
                        + (string.IsNullOrWhiteSpace(record.LastError) ? string.Empty : $"\n错误：{record.LastError}"),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(details, 1);
                row.Children.Add(details);
                var remove = new Button { Content = "移除" };
                remove.IsEnabled = !isUpdating;
                remove.Click += async (_, _) =>
                {
                    try
                    {
                        await _mapSubscriptionService.RemoveAsync(record.Id);
                        RenderRecords();
                    }
                    catch (Exception exception) { status.Text = "移除订阅失败：" + exception.Message; }
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                recordsHost.Children.Add(row);
            }
        }

        async Task UpdateAsync()
        {
            if (isUpdating) return;
            isUpdating = true;
            updateStartedAt = DateTimeOffset.Now;
            addButton.IsEnabled = false;
            updateButton.IsEnabled = false;
            RenderRecords();
            SetPackageOperationState(importButton, exportButton, true, "正在更新…");
            status.Text = "正在检查更新……";
            progressRing.IsActive = true;
            progressTimer.Start();
            try
            {
                var result = await _mapSubscriptionService.CheckAndApplyAsync();
                refreshListAfterDialog |= result.AppliedCount > 0;
                if (result.AppliedCount > 0 && !App.IsSafeMode)
                    await App.Session.RefreshMapCacheAsync();
                status.Text = result.FailedCount > 0
                    ? $"更新完成，{result.FailedCount} 个失败。"
                    : result.AppliedCount > 0
                        ? $"已更新 {result.AppliedCount} 个订阅。"
                        : "已是最新版本。";
            }
            catch (Exception exception)
            {
                status.Text = "更新失败：" + exception.Message;
            }
            finally
            {
                progressTimer.Stop();
                progressRing.IsActive = false;
                isUpdating = false;
                SetPackageOperationState(importButton, exportButton, false, null);
                addButton.IsEnabled = true;
                updateButton.IsEnabled = true;
                RenderRecords();
            }
        }

        addButton.Click += async (_, _) =>
        {
            try
            {
                await _mapSubscriptionService.AddAsync(linkBox.Text);
                linkBox.Text = string.Empty;
                RenderRecords();
                await UpdateAsync();
            }
            catch (Exception exception) { status.Text = "无法添加订阅：" + exception.Message; }
        };
        updateButton.Click += async (_, _) => await UpdateAsync();
        RenderRecords();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "地图订阅",
            Content = content,
            CloseButtonText = "完成"
        };
        dialog.Closing += (_, args) =>
        {
            if (isUpdating)
            {
                args.Cancel = true;
                status.Text = "订阅仍在更新，请等待完成后再关闭。";
            }
        };
        await dialog.ShowAsync();
        if (refreshListAfterDialog)
            await ShowListAsync();
    }

    private async Task<string?> PickPublicationFolderAsync()
    {
        try
        {
            var picker = new FolderPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "发布到此文件夹"
            };
            var result = await picker.PickSingleFolderAsync();
            return result is null || string.IsNullOrWhiteSpace(result.Path) ? null : result.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件夹选择器", exception.Message);
            return null;
        }
    }

    private static string BuildDefaultPublisherHandle()
    {
        var configuredOfficialKeyPath = Environment.GetEnvironmentVariable(
            "IDVB_MAP_OFFICIAL_PRIVATE_KEY_PATH");
        var officialKeyPaths = new[]
        {
            configuredOfficialKeyPath,
            Path.Combine(
                Directory.GetCurrentDirectory(),
                ".secrets",
                "idvb-update-2026-01-private.pem")
        };
        if (officialKeyPaths.Any(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            return MapSubscriptionProtocol.OfficialPublisherHandle;

        var name = new string(Environment.UserName.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return "@" + (string.IsNullOrWhiteSpace(name) ? "publisher" : name);
    }
}
