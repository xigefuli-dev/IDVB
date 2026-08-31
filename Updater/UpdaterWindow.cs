using IDVBuff.UpdateCore;

namespace IDVBuff.Updater;

internal sealed class UpdaterWindow : Form
{
    private readonly UpdaterLaunchOptions _options;
    private readonly Label _status = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI Semibold", 16)
    };
    private readonly RichTextBox _details = new()
    {
        ReadOnly = true,
        DetectUrls = false,
        WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Window,
        ForeColor = SystemColors.GrayText,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 10.5f),
        MinimumSize = new Size(0, 180)
    };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visible = false, Dock = DockStyle.Fill };
    private readonly Button _primary = new() { Text = "检查更新", AutoSize = true };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true };
    private UpdaterCoordinator? _coordinator;
    private CancellationTokenSource? _operation;
    private UpdateWorkflowState _state = UpdateWorkflowState.Initializing;

    public UpdaterWindow(UpdaterLaunchOptions options)
    {
        _options = options;
        Text = "Identity Vision Bridge 更新程序";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 460);
        MinimumSize = new Size(600, 400);
        Font = new Font("Segoe UI", 10);
        Icon = TryLoadIcon();
        Controls.Add(CreateContent());
        _primary.Click += Primary_Click;
        _cancel.Click += (_, _) =>
        {
            _operation?.Cancel();
            Close();
        };
        FormClosed += (_, _) =>
        {
            _operation?.Cancel();
            _operation?.Dispose();
            _coordinator?.Dispose();
        };
        Shown += async (_, _) => await InitializeAsync();
        if (_options.Background)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }
    }

    private Control CreateContent()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(32, 28, 32, 28),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _status.Margin = new Padding(3, 0, 3, 18);
        layout.Controls.Add(_status, 0, 0);
        _details.Margin = new Padding(3, 0, 3, 14);
        layout.Controls.Add(_details, 0, 1);
        _progress.Margin = new Padding(3, 0, 3, 10);
        layout.Controls.Add(_progress, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "下载时主程序可以继续使用。安装前，更新程序会请求主程序安全关闭。更新失败不会删除本地设置、地图或 IDVM 数据。",
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(0, 0),
            Padding = new Padding(12),
            BackColor = SystemColors.ControlLight,
            Margin = new Padding(3, 0, 3, 12)
        }, 0, 3);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        buttons.Controls.Add(_primary);
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 4);
        return layout;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _coordinator = new UpdaterCoordinator(_options);
            _state = UpdateWorkflowState.Checking;
            _status.Text = "正在检查更新……";
            _details.Text = $"通道：{_options.Channel}\n当前版本：{_coordinator.CurrentVersion}";
            _primary.Enabled = false;
            await CheckAsync();
        }
        catch (OperationCanceledException) when (IsDisposed || Disposing) { }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task CheckAsync()
    {
        BeginOperation();
        var available = await _coordinator!.CheckAsync(_operation!.Token);
        if (!available)
        {
            if (_options.Background) { Close(); return; }
            _state = UpdateWorkflowState.NoUpdate;
            _status.Text = "已是最新版本";
            _details.Text = $"当前版本：{_coordinator.CurrentVersion}";
            _primary.Text = "重新检查";
            _primary.Enabled = true;
            return;
        }
        _state = UpdateWorkflowState.UpdateAvailable;
        if (_options.Background) { Opacity = 1; ShowInTaskbar = true; Activate(); }
        var metadata = _coordinator.Metadata;
        _status.Text = "有可用的更新";
        var delivery = _coordinator.WillUseDeltaPackage
            ? $"将下载 {_coordinator.DeltaPackageCount} 个增量包，仅传输变更内容；若校验或还原失败，会自动安全回退到完整包。"
            : "将下载完整更新包；这是首次更新、跨版本缺少增量包，或服务器未提供可用增量包时的安全回退。";
        _details.Text = metadata is null
            ? $"当前版本：{_coordinator.CurrentVersion}\n{delivery}"
            : $"当前版本：{_coordinator.CurrentVersion}\n目标版本：{metadata.PublicVersion}\n{delivery}\n\n{metadata.ReleaseNotes}";
        _primary.Text = "下载更新";
        _primary.Enabled = true;
    }

    private async void Primary_Click(object? sender, EventArgs e)
    {
        try
        {
            if (_state is UpdateWorkflowState.NoUpdate or UpdateWorkflowState.Error)
            {
                _state = UpdateWorkflowState.Checking;
                _status.Text = "正在检查更新……";
                _details.Text = $"通道：{_options.Channel}";
                _primary.Enabled = false;
                await CheckAsync();
            }
            else if (_state == UpdateWorkflowState.UpdateAvailable) await DownloadAsync();
            else if (_state == UpdateWorkflowState.ReadyToInstall) await InstallAsync();
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed || Disposing) return;
            _state = UpdateWorkflowState.Cancelled; _status.Text = "操作已取消。"; _primary.Text = "重新检查"; _primary.Enabled = true;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task DownloadAsync()
    {
        _state = UpdateWorkflowState.Downloading;
        _status.Text = "正在下载并校验更新……";
        _progress.Value = 0; _progress.Visible = true; _primary.Enabled = false;
        BeginOperation();
        await _coordinator!.DownloadAsync(
            value =>
            {
                if (IsDisposed || Disposing) return;
                try { BeginInvoke(() => _progress.Value = value); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            },
            _operation!.Token);
        _state = UpdateWorkflowState.ReadyToInstall;
        _status.Text = "更新已下载并通过完整性校验。请保存正在进行的工作。";
        _primary.Text = _coordinator.IsInstalled ? "关闭主程序并安装" : "关闭旧版并迁移安装";
        _primary.Enabled = true;
    }

    private async Task InstallAsync()
    {
        _state = UpdateWorkflowState.RequestingShutdown;
        _status.Text = "正在请求主程序安全关闭……";
        _primary.Enabled = false;
        BeginOperation();
        await _coordinator!.InstallAsync(_operation!.Token);
    }

    private void BeginOperation() { _operation?.Dispose(); _operation = new CancellationTokenSource(); }

    private void ShowError(Exception exception)
    {
        UpdateLog.Write("Updater operation failed", exception);
        if (IsDisposed || Disposing) return;
        if (_options.Background) { Close(); return; }
        _state = UpdateWorkflowState.Error;
        _status.Text = "更新没有执行，当前版本未被修改。";
        _details.Text = $"{exception.Message}\n\n诊断日志：{UpdateLog.FilePath}";
        _primary.Text = "重试"; _primary.Enabled = true; _progress.Visible = false;
    }

    private static Icon? TryLoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "IDVB.ico");
        try { return File.Exists(path) ? new Icon(path) : null; }
        catch { return null; }
    }
}
