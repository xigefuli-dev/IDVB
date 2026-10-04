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
/// 未登录时必须提供联系 QQ 号。
/// </summary>
public sealed class FeedbackDialog : ContentDialog
{
    private readonly InfoBar _loginNoticeBar;
    private readonly TextBox _descriptionBox;
    private readonly TextBox _contactQqBox;
    private bool _isBusy;
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

        // 未登录时填写 QQ 号即可反馈，也可以选择登录。
        _loginNoticeBar = new InfoBar
        {
            IsOpen = false,
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            Message = "无需登录也可反馈；未登录时请填写联系 QQ 号，反馈数据总大小不得超过 20 MB。"
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

        _contactQqBox = new TextBox
        {
            Header = "联系方式：QQ号（未登录时必填）",
            PlaceholderText = "5–12 位数字，不能以 0 开头",
            MaxLength = 12
        };
        _contactQqBox.TextChanged += (_, _) => UpdateLoginState();
        rootPanel.Children.Add(_contactQqBox);

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
            Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
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
        Opened += (_, _) =>
        {
            AccountSession.Changed += AccountSession_Changed;
            UpdateLoginState();
        };
        Closed += (_, _) => AccountSession.Changed -= AccountSession_Changed;

        // 初始化登录校验状态
        UpdateLoginState();
    }

    private void UpdateLoginState()
    {
        bool isLoggedIn = !string.IsNullOrWhiteSpace(AccountSession.PublishToken);
        _loginNoticeBar.IsOpen = !isLoggedIn;
        _contactQqBox.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = !_isBusy && FeedbackTextValidator.IsDescriptionValid(_descriptionBox.Text)
            && (isLoggedIn || FeedbackTextValidator.IsContactQqValid(_contactQqBox.Text));
    }

    private void AccountSession_Changed(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateLoginState);

    private void DescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = _descriptionBox.Text;
        int count = FeedbackTextValidator.CalculateWeightedLength(text);
        bool isValid = FeedbackTextValidator.IsDescriptionValid(text);
        UpdateLoginState();

        if (isValid)
        {
            _counterBlock.Text = $"当前字数：{count}";
            _counterBlock.Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush");
        }
        else
        {
            _counterBlock.Text = $"当前字数：{count}（至少 11 字）";
            _counterBlock.Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush");
        }
    }

    private CancellationTokenSource? _submissionCts;

    private async void FeedbackDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 阻止对话框默认立刻关闭，执行异步打包与提交
        args.Cancel = true;

        if (_isBusy) return;

        if (string.IsNullOrWhiteSpace(AccountSession.PublishToken)
            && !FeedbackTextValidator.IsContactQqValid(_contactQqBox.Text))
        {
            _statusBlock.Text = "请提供联系 QQ 号（5–12 位数字，不能以 0 开头）。";
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
                ContactQq = string.IsNullOrWhiteSpace(AccountSession.PublishToken) ? _contactQqBox.Text.Trim() : string.Empty,
                IncludeLogs = includeLogs,
                LogsZipPath = buildResult.LogsZipPath,
                LogsZipSizeBytes = buildResult.LogsZipSizeBytes,
                IncludeDiagnostics = includeDiagnostics,
                DiagnosticsZipPath = buildResult.DiagnosticsZipPath,
                DiagnosticsZipSizeBytes = buildResult.DiagnosticsZipSizeBytes
            };

            // 3. 调用预留的官方后台接口
            var submissionResult = await _feedbackService.SubmitFeedbackAsync(payload, ct);
            AccountSession.ClearIfCurrent(submissionResult.RejectedToken);

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
        _isBusy = isBusy;
        _contactQqBox.IsEnabled = !isBusy;
        _loginNoticeBar.IsEnabled = !isBusy;
        _descriptionBox.IsEnabled = !isBusy;
        _sendLogsCheckBox.IsEnabled = !isBusy;
        _sendDiagnosticsCheckBox.IsEnabled = !isBusy;
        UpdateLoginState();
        IsSecondaryButtonEnabled = !isBusy;

        _progressRing.IsActive = isBusy;
        _progressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }
}
