using IDVBuff.Features.Maps;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

/// <summary>A native three-position slider owns input and accessibility; Composition only draws.</summary>
public sealed class ScanModeSelector : UserControl
{
    private readonly Slider _input = new() { Minimum = 0, Maximum = 2, StepFrequency = 1,
        TickFrequency = 1, Value = 1, Opacity = 0, IsThumbToolTipEnabled = false };
    private readonly TextBlock _title = new() { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly Border _fill = new() { CornerRadius = new(18), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _thumb = new() { Width = 34, Height = 34, CornerRadius = new(17),
        Background = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Left,
        BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(35, 80, 80, 80)) };
    private readonly Border _glow = new() { Width = 65, CornerRadius = new(18), Opacity = .2,
        Background = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _stars = new() { Text = "·    ˙    ·     ✧     ·      ˙    ·", FontSize = 16,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly Grid _track = new() { Height = 36 };
    private readonly UISettings _ui = new();
    private AppWindow? _window;
    private readonly List<(UIElement Element, long Token)> _ancestors = [];
    private bool _updating;
    private bool _tagOnly;
    private double _lastPosition;
    public event Action<ScanPerformanceMode>? ModeChanged;
    public ScanPerformanceMode Mode => (ScanPerformanceMode)(int)Math.Round(_input.Value);

    public ScanModeSelector()
    {
        Width = 340;
        MaxWidth = 400;
        HorizontalAlignment = HorizontalAlignment.Left;
        var stack = new StackPanel { Spacing = 12 };
        _track.Children.Add(new Border { CornerRadius = new(18),
            Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) });
        _track.Children.Add(_fill);
        _track.Children.Add(_glow);
        _track.Children.Add(_stars);
        foreach (var alignment in new[] { HorizontalAlignment.Left, HorizontalAlignment.Center, HorizontalAlignment.Right })
            _track.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new(3),
                Margin = new(14, 0, 14, 0), HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center, Opacity = .4,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false });
        _track.Children.Add(_thumb);
        _track.Children.Add(_input);
        stack.Children.Add(_title);
        stack.Children.Add(_track);
        stack.Children.Add(_hint);
        var card = new Border { Padding = new(16), CornerRadius = new(28), Child = stack,
            BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Background = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128)) };
        Content = card;
        // Translation is a XAML-injected property, not an intrinsic Visual property.
        // Register it before SizeChanged/Visibility can stop or start any animation,
        // including the glow that starts out inactive in the default Balanced mode.
        ElementCompositionPreview.SetIsTranslationEnabled(_thumb, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_glow, true);
        _input.ValueChanged += (_, _) =>
        {
            UpdateAppearance(true);
            if (!_updating) ModeChanged?.Invoke(Mode);
        };
        _input.GotFocus += (_, _) => card.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        _input.LostFocus += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        _track.SizeChanged += (_, _) => UpdateAppearance(false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMotion());
    }

    public void SetMode(ScanPerformanceMode mode, bool tagOnly)
    {
        if (mode == Mode && tagOnly == _tagOnly) return;
        _updating = true;
        _tagOnly = tagOnly;
        _input.Value = (int)mode;
        _updating = false;
        UpdateAppearance(false);
    }

    private void UpdateAppearance(bool animate)
    {
        var index = (int)Mode;
        string[] names = ["极速", "均衡", "质量"];
        string[] descriptions = ["快速筛选", "充分比较", "精细复核"];
        var budget = ScanExecutionPolicy.For(Mode).BudgetMilliseconds;
        _title.Text = $"扫描模式 · {names[index]}";
        _hint.Text = $"{descriptions[index]} · {budget} ms 内" + (_tagOnly ? " · 用于正常扫描" : "");
        AutomationProperties.SetName(_input, $"扫描模式，{names[index]}，{descriptions[index]}，{budget} 毫秒内");
        AutomationProperties.SetHelpText(_input, "使用方向键选择极速、均衡或质量。切换在下一次扫描生效。");
        _fill.Background = index == 2
            ? new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5), GradientStops = {
                new GradientStop { Color = Color.FromArgb(255, 101, 42, 166), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, 183, 105, 236), Offset = 1 } } }
            : new SolidColorBrush(index == 1 ? Microsoft.UI.Colors.DodgerBlue : Color.FromArgb(110, 190, 190, 190));
        var position = Math.Max(0, _track.ActualWidth - 34) * index / 2;
        _fill.Width = position + 34;
        var fillVisual = ElementCompositionPreview.GetElementVisual(_fill);
        fillVisual.StopAnimation("Scale.X");
        fillVisual.StopAnimation("Opacity");
        fillVisual.Scale = System.Numerics.Vector3.One;
        fillVisual.Opacity = 1;
        var visual = ElementCompositionPreview.GetElementVisual(_thumb);
        visual.StopAnimation("Translation.X");
        visual.Properties.InsertVector3("Translation", new((float)position, 0, 0));
        if (animate && CanAnimate())
        {
            var slide = visual.Compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(0, (float)_lastPosition);
            slide.InsertKeyFrame(1, (float)position);
            slide.Duration = TimeSpan.FromMilliseconds(index == 0 ? 120 : 240);
            visual.StartAnimation("Translation.X", slide);
            var fill = fillVisual.Compositor.CreateScalarKeyFrameAnimation();
            fill.InsertKeyFrame(0, (float)((_lastPosition + 34) / (position + 34)));
            fill.InsertKeyFrame(1, 1);
            fill.Duration = slide.Duration;
            fillVisual.StartAnimation("Scale.X", fill);
            if (index == 0)
            {
                var flash = fillVisual.Compositor.CreateScalarKeyFrameAnimation();
                flash.InsertKeyFrame(0, .65f); flash.InsertKeyFrame(.4f, 1); flash.InsertKeyFrame(1, .8f);
                flash.Duration = TimeSpan.FromMilliseconds(180);
                fillVisual.StartAnimation("Opacity", flash);
            }
        }
        _lastPosition = position;
        _stars.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMotion();
    }

    private bool CanAnimate() => IsLoaded && Visibility == Visibility.Visible && _ui.AnimationsEnabled
        && _ancestors.All(item => item.Element.Visibility == Visibility.Visible)
        && _window is { IsVisible: true }
        && _window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void UpdateMotion()
    {
        var glow = ElementCompositionPreview.GetElementVisual(_glow);
        var stars = ElementCompositionPreview.GetElementVisual(_stars);
        glow.StopAnimation("Translation.X");
        stars.StopAnimation("Opacity");
        stars.Opacity = .75f;
        _glow.Visibility = Visibility.Collapsed;
        if (!CanAnimate())
        {
            ElementCompositionPreview.GetElementVisual(_thumb).StopAnimation("Translation.X");
            ElementCompositionPreview.GetElementVisual(_fill).StopAnimation("Scale.X");
            ElementCompositionPreview.GetElementVisual(_fill).StopAnimation("Opacity");
        }
        if (!CanAnimate() || Mode != ScanPerformanceMode.Quality) return;
        _glow.Visibility = Visibility.Visible;
        var motion = glow.Compositor.CreateScalarKeyFrameAnimation();
        motion.InsertKeyFrame(0, 0);
        motion.InsertKeyFrame(.5f, (float)Math.Max(0, _track.ActualWidth - 65));
        motion.InsertKeyFrame(1, 0);
        motion.Duration = TimeSpan.FromSeconds(7);
        motion.IterationBehavior = AnimationIterationBehavior.Forever;
        glow.StartAnimation("Translation.X", motion);
        var shimmer = stars.Compositor.CreateScalarKeyFrameAnimation();
        shimmer.InsertKeyFrame(0, .35f); shimmer.InsertKeyFrame(.5f, .8f); shimmer.InsertKeyFrame(1, .35f);
        shimmer.Duration = TimeSpan.FromSeconds(4);
        shimmer.IterationBehavior = AnimationIterationBehavior.Forever;
        stars.StartAnimation("Opacity", shimmer);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement element)
                _ancestors.Add((element, element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMotion())));
        _window = ((App)Application.Current).MainWindow.AppWindow;
        _window.Changed += WindowChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _ui.AnimationsEnabledChanged += AnimationsChanged;
        UpdateAppearance(false);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        foreach (var (element, token) in _ancestors) element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        _ancestors.Clear();
        if (_window is not null) _window.Changed -= WindowChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _ui.AnimationsEnabledChanged -= AnimationsChanged;
        _window = null;
        UpdateMotion();
    }
    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateMotion();
    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateMotion);
}
