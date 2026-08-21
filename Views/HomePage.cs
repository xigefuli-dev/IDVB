using IDVBuff.Features.Maps;
using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed class HomePage : Page
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");

    private readonly MapRepository _mapRepository = new();
    private readonly MapRecognitionStatisticsRepository _statisticsRepository = new();
    private readonly TextBlock _mapCountValue = CreateMetricValue();
    private readonly TextBlock _successRateValue = CreateMetricValue();
    private readonly TextBlock _successRateDetail = CreateMetricDetail();
    private readonly Button _launchGameButton;
    private readonly SymbolIcon _launchGameIcon;
    private readonly TextBlock _launchGameLabel;
    private readonly DispatcherTimer _gameStatusTimer;

    public HomePage()
    {
        _launchGameIcon = new SymbolIcon(Symbol.Play) { Margin = new Thickness(0, 0, 10, 0) };
        _launchGameLabel = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
        _launchGameButton = CreateLaunchGameButton();
        _gameStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameStatusTimer.Tick += (_, _) => UpdateGameStatus();
        Content = CreateContent();
        Loaded += HomePage_Loaded;
        Unloaded += (_, _) => _gameStatusTimer.Stop();
    }

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 32,
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(new StackPanel
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
        });

        root.Children.Add(_launchGameButton);

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
        return root;
    }

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
            Background = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush"),
            CornerRadius = new CornerRadius(8),
            Shadow = new ThemeShadow()
        };
        button.Click += LaunchGameButton_Click;
        return button;
    }

    private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning())
            return;

        try
        {
            Process.Start(new ProcessStartInfo("fevergames://mygame/?gameId=73") { UseShellExecute = true });
            UpdateGameStatus();
        }
        catch
        {
            // The shell owns the custom protocol. Keep the button usable if it is not registered.
        }
    }

    private void UpdateGameStatus()
    {
        var running = IsGameRunning();
        _launchGameLabel.Text = running ? "···游戏中" : "启动游戏";
        _launchGameIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _launchGameButton.Background = FluentTheme.Brush(running
            ? "ControlFillColorDisabledBrush"
            : "AccentFillColorDefaultBrush");
        _launchGameButton.Opacity = running ? 0.72 : 1;
    }

    private static bool IsGameRunning() => Process.GetProcessesByName("dwrg").Length > 0;

    private static Border CreateMetricCard(
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
            Background = FluentTheme.Brush("AccentFillColorTertiaryBrush"),
            Child = new SymbolIcon(symbol)
            {
                Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
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
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGameStatus();
        _gameStatusTimer.Start();
        _mapCountValue.Text = "…";
        _successRateValue.Text = "…";
        _successRateDetail.Text = string.Empty;
        try
        {
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
    }

    private static TextBlock CreateMetricValue() => new()
    {
        Text = "…",
        FontSize = 30,
        FontWeight = FontWeights.SemiBold,
        Foreground = PrimaryTextBrush
    };

    private static TextBlock CreateMetricDetail() => new()
    {
        FontSize = 12,
        Foreground = SecondaryTextBrush
    };
}
