using IDVBuff.Features.Maps;
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

    public HomePage()
    {
        Content = CreateContent();
        Loaded += HomePage_Loaded;
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
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
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
