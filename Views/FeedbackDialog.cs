using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using IDVBuff.Features.Accounts;
using IDVBuff.Features.Feedback;

namespace IDVBuff.Views;

/// <summary>
/// 反馈问题对话框。
/// 提供问题描述输入（中文汉字算2字符，必须大于10字符）、日志与诊断数据发送选项，
/// 并执行安全的异步后台打包与官网后台接口提交。
/// 强制要求必须登录后才能使用。
/// </summary>
public sealed class FeedbackDialog : ContentDialog
{
    private readonly InfoBar _loginNoticeBar;
    private readonly TextBox _descriptionBox;
    private readonly TextBlock _counterBlock;
    private readonly CheckBox _sendLogsCheckBox;
    private readonly CheckBox _sendDiagnosticsCheckBox;
    private readonly ProgressRing _progressRing;
    private readonly TextBlock _statusBlock;
    private readonly IFeedbackService _feedbackService;

    public FeedbackDialog(XamlRoot xamlRoot, IFeedbackService? feedbackService = null)
    {
        XamlRoot = xamlRoot;
        _feedbackService = feedbackService ?? OfficialFeedbackService.Instance;

        Title = "反馈问题";
        PrimaryButtonText = "立即反馈";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        // 对话框整体内容容器
        var rootPanel = new StackPanel
        {
            Spacing = 14,
            MinWidth = 460
        };

        // 登录提示条（未登录时强制提示并禁用提交）
        _loginNoticeBar = new InfoBar
        {
            IsOpen = false,
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            Message = "反馈功能必须登录社区账户后才能使用。"
        };
        var loginButton = new Button
        {
            Content = "立即登录",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        loginButton.Click += async (s, e) =>
        {
            try
            {
                loginButton.IsEnabled = false;
                await AccountSession.LoginAsync();
                UpdateLoginState();
            }
            catch (Exception ex)
            {
                _loginNoticeBar.Message = $"登录未完成: {ex.Message}";
            }
            finally
            {
                loginButton.IsEnabled = true;
            }
        };
        _loginNoticeBar.ActionButton = loginButton;
        rootPanel.Children.Add(_loginNoticeBar);

        // 描述说明标签
        var descHeader = new TextBlock
        {
            Text = "问题描述",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14
        };
        rootPanel.Children.Add(descHeader);

        // 多行描述输入框
        _descriptionBox = new TextBox
        {
            PlaceholderText = "请详细描述您遇到的问题或改进建议...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 220
        };
        _descriptionBox.TextChanged += DescriptionBox_TextChanged;
        rootPanel.Children.Add(_descriptionBox);

        // 实时字数与校验提示
        _counterBlock = new TextBlock
        {
            Text = "当前字数：0（至少 11 字）",
            FontSize = 12,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        };
        rootPanel.Children.Add(_counterBlock);

        // 选项分割说明
        var optionsHeader = new TextBlock
        {
            Text = "附加信息",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        };
        rootPanel.Children.Add(optionsHeader);

        var optionsPanel = new StackPanel { Spacing = 6 };

        _sendLogsCheckBox = new CheckBox
        {
            Content = "发送日志",
            IsChecked = false
        };
        optionsPanel.Children.Add(_sendLogsCheckBox);

        _sendDiagnosticsCheckBox = new CheckBox
        {
            Content = "发送诊断数据",
            IsChecked = false
        };
        optionsPanel.Children.Add(_sendDiagnosticsCheckBox);

        rootPanel.Children.Add(optionsPanel);

        // 提交与进度状态展示区
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _progressRing = new ProgressRing
        {
            IsActive = false,
            Visibility = Visibility.Collapsed,
            Width = 20,
            Height = 20
        };
        statusPanel.Children.Add(_progressRing);

        _statusBlock = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        statusPanel.Children.Add(_statusBlock);

        rootPanel.Children.Add(statusPanel);

        Content = rootPanel;

        // 绑定主按钮与取消按钮点击事件
        PrimaryButtonClick += FeedbackDialog_PrimaryButtonClick;
        CloseButtonClick += (_, _) => _submissionCts?.Cancel();

        // 初始化登录校验状态
        UpdateLoginState();
    }

    private void UpdateLoginState()
    {
        bool isLoggedIn = AccountSession.Identity != null && !string.IsNullOrWhiteSpace(AccountSession.PublishToken);
        _loginNoticeBar.IsOpen = !isLoggedIn;
        IsPrimaryButtonEnabled = isLoggedIn && FeedbackTextValidator.IsDescriptionValid(_descriptionBox.Text);
    }

    private void DescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = _descriptionBox.Text;
        int count = FeedbackTextValidator.CalculateWeightedLength(text);
        bool isValid = FeedbackTextValidator.IsDescriptionValid(text);
        bool isLoggedIn = AccountSession.Identity != null && !string.IsNullOrWhiteSpace(AccountSession.PublishToken);

        IsPrimaryButtonEnabled = isValid && isLoggedIn;

        if (isValid)
        {
            _counterBlock.Text = $"当前字数：{count}";
            _counterBlock.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush;
        }
        else
        {
            _counterBlock.Text = $"当前字数：{count}（至少 11 字）";
            _counterBlock.Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        }
    }

    private CancellationTokenSource? _submissionCts;

    private async void FeedbackDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 阻止对话框默认立刻关闭，执行异步打包与提交
        args.Cancel = true;

        if (AccountSession.Identity == null || string.IsNullOrWhiteSpace(AccountSession.PublishToken))
        {
            _statusBlock.Text = "反馈功能必须登录社区账户后才能使用，请先点击上方“立即登录”。";
            UpdateLoginState();
            return;
        }

        if (!FeedbackTextValidator.IsDescriptionValid(_descriptionBox.Text))
        {
            _statusBlock.Text = "请至少输入 11 字的问题描述。";
            return;
        }

        // 切换至提交状态，禁用输入控件
        SetBusyState(true);
        _statusBlock.Text = "正在整理并打包日志与诊断数据...";

        string? tempDirectory = null;
        _submissionCts?.Dispose();
        _submissionCts = new CancellationTokenSource();
        var ct = _submissionCts.Token;

        try
        {
            bool includeLogs = _sendLogsCheckBox.IsChecked == true;
            bool includeDiagnostics = _sendDiagnosticsCheckBox.IsChecked == true;

            // 1. 在后台安全打包
            var buildResult = await FeedbackPackageBuilder.BuildPackagesAsync(
                includeLogs,
                includeDiagnostics);

            ct.ThrowIfCancellationRequested();
            tempDirectory = buildResult.OutputDirectory;

            _statusBlock.Text = "正在提交反馈数据至官网后台...";

            // 2. 构造提交载荷
            var payload = new FeedbackSubmissionPayload
            {
                Description = _descriptionBox.Text.Trim(),
                IncludeLogs = includeLogs,
                LogsZipPath = buildResult.LogsZipPath,
                LogsZipSizeBytes = buildResult.LogsZipSizeBytes,
                IncludeDiagnostics = includeDiagnostics,
                DiagnosticsZipPath = buildResult.DiagnosticsZipPath,
                DiagnosticsZipSizeBytes = buildResult.DiagnosticsZipSizeBytes
            };

            // 3. 调用预留的官方后台接口
            var submissionResult = await _feedbackService.SubmitFeedbackAsync(payload, ct);

            if (submissionResult.Success)
            {
                _statusBlock.Text = string.IsNullOrWhiteSpace(submissionResult.Message)
                    ? "反馈已提交成功！感谢您的反馈。"
                    : submissionResult.Message;

                _progressRing.IsActive = false;
                _progressRing.Visibility = Visibility.Collapsed;

                // 稍作停留让用户看到成功提示，然后关闭对话框
                await Task.Delay(1200, ct);
                Hide();
            }
            else
            {
                _statusBlock.Text = $"提交失败: {submissionResult.Message}";
                SetBusyState(false);
            }
        }
        catch (OperationCanceledException)
        {
            _statusBlock.Text = "已取消提交。";
            SetBusyState(false);
        }
        catch (Exception exception)
        {
            // 全局容错，防止任何异常导致主程序崩溃
            _statusBlock.Text = $"发生异常: {exception.Message}";
            SetBusyState(false);
        }
        finally
        {
            // 4. 清理临时生成的 ZIP 目录
            if (!string.IsNullOrEmpty(tempDirectory))
            {
                FeedbackPackageBuilder.CleanupTempDirectory(tempDirectory);
            }
        }
    }

    private void SetBusyState(bool isBusy)
    {
        _descriptionBox.IsEnabled = !isBusy;
        _sendLogsCheckBox.IsEnabled = !isBusy;
        _sendDiagnosticsCheckBox.IsEnabled = !isBusy;
        bool isLoggedIn = AccountSession.Identity != null && !string.IsNullOrWhiteSpace(AccountSession.PublishToken);
        IsPrimaryButtonEnabled = !isBusy && isLoggedIn && FeedbackTextValidator.IsDescriptionValid(_descriptionBox.Text);
        IsSecondaryButtonEnabled = !isBusy;

        _progressRing.IsActive = isBusy;
        _progressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }
}
