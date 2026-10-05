using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace IDVBuff.Views;

public sealed class SponsorshipPage : Page
{
    private readonly Image _wechat = new();
    private readonly Image _alipay = new();
    private readonly Border _banner = new();
    private readonly Grid _payments = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error, IsClosable = false,
        Title = "收款码不可用",
        Message = "收款资源无法验证，已停止显示。请从 IDVB 官方渠道重新获取程序。"
    };
    private int _loadRevision;

    public event EventHandler? BackRequested;

    public SponsorshipPage()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = CreateContent();
        Loaded += LoadArtwork;
        Unloaded += (_, _) =>
        {
            ++_loadRevision;
            _wechat.Source = _alipay.Source = null;
            _payments.Visibility = Visibility.Collapsed;
        };
    }

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(32, 24, 32, 36), MaxWidth = 900, Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var back = new Button { Content = new SymbolIcon(Symbol.Back), Padding = new Thickness(8) };
        AutomationProperties.SetName(back, "返回");
        ToolTipService.SetToolTip(back, "返回");
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        heading.Children.Add(back);
        heading.Children.Add(Label("赞助支持", 28, primary: true));
        root.Children.Add(heading);
        root.Children.Add(CreateBanner());
        root.Children.Add(Label("每一份支持，都会成为 IDVB 继续维护与探索的动力。赞助完全自愿，感谢你的陪伴。", 14));
        _payments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _payments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _payments.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _payments.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var wechat = CreatePaymentCard("微信支付", "佳霖（**东）", "使用微信扫一扫", _wechat,
            new Rect(208, 262, 412, 412), 828, 1124);
        var alipay = CreatePaymentCard("支付宝", "对镜自演（**东）", "使用支付宝扫一扫", _alipay,
            new Rect(296, 922, 1116, 1116), 1708, 2560);
        Grid.SetColumn(alipay, 1);
        _payments.Children.Add(wechat);
        _payments.Children.Add(alipay);
        _payments.Visibility = Visibility.Collapsed;
        _payments.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            _payments.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(alipay, narrow ? 0 : 1);
            Grid.SetRow(alipay, narrow ? 1 : 0);
        };
        root.Children.Add(_payments);
        root.Children.Add(_error);
        root.Children.Add(Label("付款前，请核对支付应用显示的收款人信息与上方一致。", 13));
        root.Children.Add(CreateAfdianCard());
        return root;
    }

    private Border CreateBanner()
    {
        var surface = new Grid { MinHeight = 160, Padding = new Thickness(28, 22, 28, 22) };
        var glow = new Border
        {
            Opacity = .12, CornerRadius = new CornerRadius(16), IsHitTestVisible = false
        };
        var accent = (SolidColorBrush)FluentTheme.Brush(this, IDVBuff.Appearance.ThemeToken.Accent);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        var clear = new GradientStop { Offset = 0 };
        var color = new GradientStop { Offset = 1 };
        gradient.GradientStops.Add(clear);
        gradient.GradientStops.Add(color);
        glow.Background = gradient;
        void RefreshGradient()
        {
            color.Color = accent.Color;
            clear.Color = Windows.UI.Color.FromArgb(0, accent.Color.R, accent.Color.G, accent.Color.B);
            glow.Visibility = FluentTheme.Snapshot(this).IsHighContrast
                ? Visibility.Collapsed : Visibility.Visible;
        }
        void ThemeChanged(IDVBuff.Appearance.ThemeSnapshot snapshot) => RefreshGradient();
        long accentToken = 0;
        glow.Loaded += (_, _) =>
        {
            accentToken = accent.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => RefreshGradient());
            IDVBuff.Presentation.Theming.ThemeService.For(this).Changed += ThemeChanged;
            RefreshGradient();
        };
        glow.Unloaded += (_, _) =>
        {
            accent.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, accentToken);
            IDVBuff.Presentation.Theming.ThemeService.For(this).Changed -= ThemeChanged;
        };
        var decoration = new Grid { IsHitTestVisible = false, Margin = new Thickness(-20) };
        decoration.Children.Add(glow);
        surface.Children.Add(decoration);
        var words = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        brand.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png")),
            Width = 40, Height = 40, Stretch = Stretch.Uniform
        });
        brand.Children.Add(Label("IDVB", 24, primary: true));
        words.Children.Add(brand);
        words.Children.Add(Label("让探索继续。", 34, primary: true));
        surface.Children.Add(words);
        _banner.Background = FluentTheme.CardBrush(this);
        _banner.BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush");
        _banner.BorderThickness = new Thickness(1);
        _banner.CornerRadius = new CornerRadius(12);
        _banner.Child = surface;
        _banner.SizeChanged += (_, args) => _banner.Clip = new RectangleGeometry
            { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        // Only the subtle accent decoration breathes; real text remains still and readable.
        _ = new GentleSponsorshipMotion(decoration);
        return _banner;
    }

    private Border CreatePaymentCard(string title, string recipient, string instruction, Image image,
        Rect crop, double originalWidth, double originalHeight)
    {
        var body = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(Label(title, 18, primary: true));
        // Canvas measures the full-sized image independently of the crop viewport.
        // Grid would constrain the image's layout slot and clip it BEFORE a render
        // translation, leaving only the top-left fragment of the payment code.
        // Only the Canvas viewport clips; verified PNG bytes remain untouched.
        image.Width = originalWidth;
        image.Height = originalHeight;
        image.Stretch = Stretch.Fill;
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(image, -crop.X);
        Canvas.SetTop(image, -crop.Y);
        AutomationProperties.SetName(image, title + "收款码，收款人 " + recipient);
        var viewport = new Canvas
        {
            Width = crop.Width, Height = crop.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, crop.Width, crop.Height) }
        };
        viewport.Children.Add(image);
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Viewbox { MaxWidth = 224, MaxHeight = 224, Stretch = Stretch.Uniform, Child = viewport }
        });
        var name = Label(recipient, 14, primary: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(name);
        var hint = Label(instruction, 12);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(hint);
        return Card(body);
    }

    private Border CreateAfdianCard()
    {
        var content = new Grid { ColumnSpacing = 16 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Label("也可以通过爱发电支持", 15, primary: true));
        copy.Children.Add(Label("选择你习惯的方式，点击后在浏览器打开。", 12));
        content.Children.Add(copy);
        var button = new Button { Content = "前往爱发电 ↗", VerticalAlignment = VerticalAlignment.Center };
        var action = new SponsorshipAction(async uri => await Launcher.LaunchUriAsync(uri),
            enabled => button.IsEnabled = enabled, async () => await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "链接未打开",
                Content = "无法打开浏览器，请稍后重试。", CloseButtonText = "知道了"
            }.ShowThemedAsync());
        button.Click += async (_, _) => await action.ClickAsync();
        Grid.SetColumn(button, 1);
        content.Children.Add(button);
        return Card(content);
    }

    private Border Card(UIElement content) => new()
    {
        Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(10),
        Background = FluentTheme.CardBrush(this), BorderThickness = new Thickness(1),
        BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"), Child = content
    };

    private TextBlock Label(string text, double size, bool primary = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = FluentTheme.Brush(this, primary ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")
    };

    private async void LoadArtwork(object sender, RoutedEventArgs args)
    {
        var revision = ++_loadRevision;
        _error.IsOpen = false;
        _payments.Visibility = Visibility.Collapsed;
        try
        {
            // Verify both recipients before exposing either payment code.
            var wechat = SponsorshipAssets.ReadVerified(SponsorshipAsset.WeChat);
            var alipay = SponsorshipAssets.ReadVerified(SponsorshipAsset.Alipay);

            var wechatBitmap = await DecodeAsync(wechat);
            var alipayBitmap = await DecodeAsync(alipay);

            if (revision != _loadRevision) return;
            _wechat.Source = wechatBitmap;
            _alipay.Source = alipayBitmap;

            _payments.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            if (revision != _loadRevision) return;
            _wechat.Source = _alipay.Source = null;
            _payments.Visibility = Visibility.Collapsed;
            _error.IsOpen = true;
            System.Diagnostics.Debug.WriteLine($"[Sponsorship] Artwork rejected: {exception}");
        }
    }

    private static async Task<BitmapImage> DecodeAsync(byte[] verifiedBytes)
    {
        using var bytes = new MemoryStream(verifiedBytes, writable: false);
        using var stream = bytes.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
