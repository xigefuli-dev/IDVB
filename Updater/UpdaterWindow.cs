using IDVBuff.UpdateCore;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Updater;

internal sealed class UpdaterWindow : Window
{
    private readonly UpdaterLaunchOptions _options;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly Button _primary = new() { Content = "检查更新", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _cancel = new() { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right };
    private UpdaterCoordinator? _coordinator;
    private CancellationTokenSource? _operation;
    private UpdateWorkflowState _state = UpdateWorkflowState.Initializing;

    public UpdaterWindow(UpdaterLaunchOptions options)
    {
        _options = options;
        Title = "Identity Vision Bridge 更新程序";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 500));
        Content = CreateContent();
        _primary.Click += Primary_Click;
        _cancel.Click += (_, _) => _operation?.Cancel();
        Closed += (_, _) =>
        {
            _operation?.Cancel();
            _operation?.Dispose();
            _coordinator?.Dispose();
        };
        _ = InitializeAsync();
    }

    private FrameworkElement CreateContent()
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_primary);

        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(36) };
        panel.Children.Add(new TextBlock
        {
            Text = "Identity Vision Bridge",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "引导式更新",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(_status);
        panel.Children.Add(_details);
        panel.Children.Add(_progress);
        panel.Children.Add(new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = "下载时主程序可以继续使用。安装前，更新程序会提示保存工作，并请求主程序完成安全关闭。更新失败不会删除本地设置、地图或 IDVM 数据。",
                TextWrapping = TextWrapping.Wrap
            }
        });
        panel.Children.Add(buttons);
        return new ScrollViewer { Content = panel };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _coordinator = new UpdaterCoordinator(_options);
            _state = UpdateWorkflowState.Checking;
            _status.Text = "正在验证更新源并检查新版本……";
            _details.Text = $"通道：{_options.Channel}\n当前版本：{_coordinator.CurrentVersion}";
            _primary.IsEnabled = false;
            await CheckAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task CheckAsync()
    {
        BeginOperation();
        var available = await _coordinator!.CheckAsync(_operation!.Token);
        if (!available)
        {
            _state = UpdateWorkflowState.NoUpdate;
            _status.Text = "当前已是最新版本。";
            _details.Text = $"当前版本：{_coordinator.CurrentVersion}";
            _primary.Content = "重新检查";
            _primary.IsEnabled = true;
            return;
        }

        _state = UpdateWorkflowState.UpdateAvailable;
        var metadata = _coordinator.Metadata;
        _status.Text = _coordinator.IsInstalled ? "发现可用更新。" : "发现新的 Velopack 安装版本。";
        _details.Text = metadata is null
            ? $"当前版本：{_coordinator.CurrentVersion}"
            : $"当前版本：{_coordinator.CurrentVersion}\n目标版本：{metadata.PublicVersion}\n\n{metadata.ReleaseNotes}";
        _primary.Content = "下载更新";
        _primary.IsEnabled = true;
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_state)
            {
                case UpdateWorkflowState.NoUpdate:
                case UpdateWorkflowState.Error:
                    _state = UpdateWorkflowState.Checking;
                    _primary.IsEnabled = false;
                    await CheckAsync();
                    break;
                case UpdateWorkflowState.UpdateAvailable:
                    await DownloadAsync();
                    break;
                case UpdateWorkflowState.ReadyToInstall:
                    await InstallAsync();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _state = UpdateWorkflowState.Cancelled;
            _status.Text = "操作已取消。";
            _primary.Content = "重新检查";
            _primary.IsEnabled = true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task DownloadAsync()
    {
        _state = UpdateWorkflowState.Downloading;
        _status.Text = "正在下载并校验更新……";
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _primary.IsEnabled = false;
        BeginOperation();
        IProgress<int> progress = new Progress<int>(value => _progress.Value = value);
        await _coordinator!.DownloadAsync(value => progress.Report(value), _operation!.Token);
        _state = UpdateWorkflowState.ReadyToInstall;
        _status.Text = "更新已下载并通过完整性校验。请保存正在进行的工作。";
        _primary.Content = _coordinator.IsInstalled ? "关闭主程序并安装" : "关闭旧版并迁移安装";
        _primary.IsEnabled = true;
    }

    private async Task InstallAsync()
    {
        _state = UpdateWorkflowState.RequestingShutdown;
        _status.Text = "正在请求主程序安全关闭……";
        _primary.IsEnabled = false;
        BeginOperation();
        await _coordinator!.InstallAsync(_operation!.Token);
    }

    private void BeginOperation()
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
    }

    private void ShowError(Exception exception)
    {
        UpdateLog.Write("Updater operation failed", exception);
        _state = UpdateWorkflowState.Error;
        _status.Text = "更新没有执行。当前版本未被修改。";
        _details.Text = $"{exception.Message}\n\n诊断日志：{UpdateLog.FilePath}";
        _primary.Content = "重试";
        _primary.IsEnabled = true;
        _progress.Visibility = Visibility.Collapsed;
    }
}
