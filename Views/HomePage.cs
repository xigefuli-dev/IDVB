using IDVBuff.Features.Maps;
using IDVBuff.Features.Announcements;
using IDVBuff.Features.GameLaunch;
using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class HomePage : Page
{
    private Brush PrimaryTextBrush => FluentTheme.Brush(this, "TextFillColorPrimaryBrush");
    private Brush SecondaryTextBrush => FluentTheme.Brush(this, "TextFillColorSecondaryBrush");

    private readonly MapRepository _mapRepository = new();
    private readonly MapRecognitionStatisticsRepository _statisticsRepository = new();
    private readonly TaskCompletionSource _initialReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _mapCountValue = CreateMetricValue();
    private readonly TextBlock _successRateValue = CreateMetricValue();
    private readonly TextBlock _successRateDetail = CreateMetricDetail();
    private readonly Button _launchGameButton;
    private readonly SymbolIcon _launchGameIcon;
    private readonly TextBlock _launchGameLabel;
    private readonly DispatcherTimer _gameStatusTimer;
    private readonly ScanModeSelector _scanModeSelector = new();
    private readonly ScanModeBloom _scanModeBloom = new();
    private readonly TextBlock _scanModeSaveError;
    private bool _savingScanMode;
    private ScanPerformanceMode? _requestedScanMode;
    private ScanPerformanceMode _savedScanMode = ScanPerformanceMode.Balanced;
    private bool _savedTagOnly;
    private int _scanModeSelectionRevision;

    public event Action<Color, bool>? ScanModeVisualChanged;
    public Color CurrentScanModeAccent => _scanModeSelector.AccentColor;

    public HomePage()
    {
        _launchGameIcon = new SymbolIcon(Symbol.Play) { Margin = new Thickness(0, 0, 10, 0) };
        _launchGameLabel = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
        _launchGameButton = CreateLaunchGameButton();
        _gameStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameStatusTimer.Tick += (_, _) => UpdateGameStatus();
        _scanModeSaveError = new TextBlock
        {
            FontSize = 12,
            Foreground = FluentTheme.Brush(this, "SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        _scanModeSelector.ModeChanged += ScanModeSelector_ModeChanged;
        _scanModeBloom.AccentColor = _scanModeSelector.AccentColor;
        void UpdateAmbient(IDVBuff.Appearance.ThemeSnapshot theme) => _scanModeBloom.Visibility =
            ScanModeSelector.AllowsGlass(theme) ? Visibility.Visible : Visibility.Collapsed;
        UpdateAmbient(FluentTheme.Snapshot(this));
        FluentTheme.Observe(this, UpdateAmbient);
        Content = CreateContent();
        Loaded += HomePage_Loaded;
        Unloaded += (_, _) =>
        {
            _gameStatusTimer.Stop();
        };
    }

    private FrameworkElement CreateContent()
    {
        var page = new Grid
        {
            Margin = new Thickness(40, 36, 40, 64),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _scanModeBloom.Margin = new Thickness(0, -36, -40, 0);
        _scanModeBloom.HorizontalAlignment = HorizontalAlignment.Right;
        _scanModeBloom.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetZIndex(_scanModeBloom, -1);
        page.Children.Add(_scanModeBloom);

        var root = new StackPanel { Spacing = 32 };
        var topBand = new Grid { MinHeight = 344 };
        topBand.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });
        topBand.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(452) });
        topBand.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topBand.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        var welcome = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "欢迎使用 Identity Vision Bridge",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush
                },
                new TextBlock
                {
                    Text = "集中查看地图资产与识别运行概况。",
                    FontSize = 14,
                    Foreground = SecondaryTextBrush
                }
            }
        };
        var primaryControls = new StackPanel
        {
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                welcome,
                CreateGameShelf(),
                CreateGameLaunchActions()
            }
        };
        Grid.SetColumn(primaryControls, 0);
        topBand.Children.Add(primaryControls);

        var scanControls = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                _scanModeSelector,
                _scanModeSaveError
            }
        };
        Grid.SetColumn(scanControls, 1);
        topBand.Children.Add(scanControls);
        root.Children.Add(topBand);

        page.SizeChanged += (_, args) =>
            UpdateResponsiveLayout(args.NewSize.Width, topBand, primaryControls, scanControls);

        var cards = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16
        };
        cards.Children.Add(CreateMetricCard(
            "地图数量",
            "当前地图库中的地图总数",
            Symbol.Library,
            _mapCountValue));
        cards.Children.Add(CreateMetricCard(
            "识别成功率",
            "产生有效对齐的识别会话占比",
            Symbol.Accept,
            _successRateValue,
            _successRateDetail));

        var section = new StackPanel { Spacing = 14 };
        section.Children.Add(new TextBlock
        {
            Text = "概览",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        section.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = cards
        });
        root.Children.Add(section);
        page.Children.Add(root);
        return page;
    }

    private void UpdateResponsiveLayout(double availableWidth, Grid topBand,
        FrameworkElement primaryControls, FrameworkElement scanControls)
    {
        // WinUI layout units are DIPs. Sizing from the available DIP width keeps
        // the bloom and card stable across display scaling as well as resolution.
        var bloomWidth = Math.Clamp(availableWidth * .52, 560, 860);
        _scanModeBloom.Width = bloomWidth;
        _scanModeBloom.Height = Math.Clamp(bloomWidth * .46, 300, 396);

        var stackControls = availableWidth < 980;
        topBand.MinHeight = stackControls ? 0 : 344;
        topBand.RowDefinitions[1].Height = stackControls
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetColumnSpan(primaryControls, stackControls ? 2 : 1);
        Grid.SetRow(scanControls, stackControls ? 1 : 0);
        Grid.SetColumn(scanControls, stackControls ? 0 : 1);
        Grid.SetColumnSpan(scanControls, stackControls ? 2 : 1);
        scanControls.HorizontalAlignment = stackControls
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        scanControls.Margin = stackControls
            ? new Thickness(0, 24, 0, 0)
            : new Thickness(0);
    }

    internal void SetAmbientAccent(Color color) => _scanModeBloom.AccentColor = color;

    public Task InitialReady => _initialReady.Task;

    private Button CreateLaunchGameButton()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _launchGameIcon, _launchGameLabel }
        };
        var button = new Button
        {
            Width = 300,
            Height = 58,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = content,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            CornerRadius = new CornerRadius(8),
            Shadow = new ThemeShadow()
        };
        button.Click += LaunchGameButton_Click;
        return button;
    }

    private async void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning())
            return;

        if (!FeverGamesGameLauncher.TryLaunch(out var failureReason))
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法启动游戏",
                Content = failureReason,
                CloseButtonText = "知道了"
            }.ShowThemedAsync();
            return;
        }

        UpdateGameStatus();
    }

    private void UpdateGameStatus()
    {
        var running = IsGameRunning();
        _launchGameLabel.Text = running ? "···游戏中" : "启动游戏";
        _launchGameIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _launchGameButton.IsEnabled = !running;
    }

    private static bool IsGameRunning() => Process.GetProcessesByName("dwrg").Length > 0;

    private Border CreateMetricCard(
        string title,
        string description,
        Symbol symbol,
        TextBlock value,
        TextBlock? detail = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconSurface = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = FluentTheme.Brush(this, "AccentFillColorTertiaryBrush"),
            Child = new SymbolIcon(symbol)
            {
                Foreground = FluentTheme.Brush(this, "TextOnAccentFillColorPrimaryBrush")
            }
        };
        grid.Children.Add(iconSurface);

        var text = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 14,
                    Foreground = SecondaryTextBrush
                },
                value,
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    Foreground = SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        if (detail is not null)
            text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Width = 320,
            MinHeight = 150,
            Padding = new Thickness(20),
            Background = FluentTheme.CardBrush(this),
            BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateGameStatus();
            _gameStatusTimer.Start();
            var selectionRevision = _scanModeSelectionRevision;
            var savedScanSettings = await new MapRuntimeSettingsRepository().LoadAsync();
            if (!_savingScanMode && selectionRevision == _scanModeSelectionRevision)
            {
                _savedScanMode = savedScanSettings.ScanPerformanceMode;
                _savedTagOnly = savedScanSettings.SelectMapByTagsEnabled;
                _scanModeSelector.SetMode(
                    _savedScanMode,
                    _savedTagOnly);
                ScanModeVisualChanged?.Invoke(_scanModeSelector.AccentColor, false);
            }
            _mapCountValue.Text = "…";
            _successRateValue.Text = "…";
            _successRateDetail.Text = string.Empty;

            // 公告仍在后台刷新，以便未读红点及时更新；但独立窗口必须等主界面完整呈现。
            _ = Task.Run(async () =>
            {
                try
                {
                    // 本地缓存只用于窗口的即时首帧；启动检查必须刷新远端，
                    // 否则首次成功拉取后会永久看不到后来发布的公告。
                    var important = await AnnouncementService.Instance.GetImportantUnreadAsync(forceRefresh: true);
                    if (important != null)
                    {
                        await App.MainWindowPresentationCompleted;
                        DispatcherQueue?.TryEnqueue(() => AnnouncementWindow.Show(important.Id));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HomePage] 公告后台初始化异常: {ex.Message}");
                }
            });

            var mapsTask = _mapRepository.GetMapsAsync();
            var statisticsTask = _statisticsRepository.GetAsync();
            await Task.WhenAll(mapsTask, statisticsTask);

            var statistics = await statisticsTask;
            _mapCountValue.Text = (await mapsTask).Count.ToString();
            _successRateValue.Text = statistics.TotalAttempts == 0
                ? "—"
                : statistics.SuccessRate.ToString("P0");
            _successRateDetail.Text = statistics.TotalAttempts == 0
                ? "暂无识别会话"
                : $"{statistics.SuccessfulAttempts} / {statistics.TotalAttempts} 次成功";
        }
        catch
        {
            _mapCountValue.Text = "—";
            _successRateValue.Text = "—";
            _successRateDetail.Text = "数据暂时不可用";
        }
        finally
        {
            _initialReady.TrySetResult();
        }
    }

    private static TextBlock CreateMetricValue()
    {
        var text = new TextBlock { Text = "…", FontSize = 30, FontWeight = FontWeights.SemiBold };
        text.Foreground = FluentTheme.Brush(text, "TextFillColorPrimaryBrush");
        return text;
    }

    private static TextBlock CreateMetricDetail()
    {
        var text = new TextBlock { FontSize = 12 };
        text.Foreground = FluentTheme.Brush(text, "TextFillColorSecondaryBrush");
        return text;
    }

    private async void ScanModeSelector_ModeChanged(ScanPerformanceMode mode)
    {
        _scanModeSelectionRevision++;
        ScanModeVisualChanged?.Invoke(ScanModeSelector.GetAccentColor(mode), true);
        _requestedScanMode = mode;
        if (_savingScanMode)
            return;

        _savingScanMode = true;
        _scanModeSaveError.Visibility = Visibility.Collapsed;
        try
        {
            // Preserve the last click if the user changes modes while the
            // preceding settings write is still in flight.
            while (_requestedScanMode is { } requested)
            {
                _requestedScanMode = null;
                try
                {
                    if (App.CurrentSession is { } session)
                        await session.SetScanPerformanceModeAsync(requested);
                    else
                    {
                        var repository = new MapRuntimeSettingsRepository();
                        var settings = await repository.LoadAsync();
                        settings.ScanPerformanceMode = requested;
                        await repository.SaveAsync(settings);
                    }
                    _savedScanMode = requested;
                    _scanModeSaveError.Visibility = Visibility.Collapsed;
                }
                catch (Exception exception)
                {
                    _scanModeSaveError.Text =
                        $"扫描模式保存失败：{exception.Message}";
                    _scanModeSaveError.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            _savingScanMode = false;
            var currentSettings = App.CurrentSession?.Settings;
            var saved = currentSettings?.ScanPerformanceMode ?? _savedScanMode;
            if (_scanModeSelector.Mode != saved)
            {
                _scanModeSelector.SetMode(saved,
                    currentSettings?.SelectMapByTagsEnabled ?? _savedTagOnly);
                ScanModeVisualChanged?.Invoke(_scanModeSelector.AccentColor, true);
            }
        }
    }
}
