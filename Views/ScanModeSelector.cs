using System.Numerics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

/// <summary>
/// Three independent scan-budget choices. The native slider owns input and accessibility;
/// the surrounding layers provide the segmented visual treatment and mode-specific motion.
/// </summary>
public sealed partial class ScanModeSelector : UserControl
{
    private static readonly string[] ModeNames = ["极速", "均衡", "质量"];
    private static readonly string[] ModeDescriptions =
    [
        "更快的响应速度",
        "兼顾响应速度与扫描质量",
        "更细致的扫描结果"
    ];
    private static readonly Color[] ModeColors =
    [
        Color.FromArgb(255, 50, 218, 137),
        Color.FromArgb(255, 48, 151, 255),
        Color.FromArgb(255, 182, 91, 242)
    ];

    private readonly Slider _input = new()
    {
        Minimum = 0,
        Maximum = 2,
        StepFrequency = 1,
        TickFrequency = 1,
        Value = 1,
        Opacity = 0,
        IsThumbToolTipEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly TextBlock _title = new()
    {
        Text = "扫描模式",
        FontSize = 18,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _hint = new()
    {
        FontSize = 12,
        Opacity = .75,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Grid _track = new() { Height = 58 };
    private readonly Border _trackSurface = new()
    {
        CornerRadius = new CornerRadius(29),
        Background = new SolidColorBrush(Color.FromArgb(62, 128, 128, 128)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(42, 128, 128, 128)),
        BorderThickness = new Thickness(1)
    };
    private readonly Border _glowOuter = CreateEffectPill(88, 44);
    private readonly Border _glowInner = CreateEffectPill(72, 36);
    private readonly Border _fastGlow = CreateEffectPill(46, 23);
    private readonly Border _qualityHaloOuter = CreateEffectPill(62, 31);
    private readonly Border _qualityHaloInner = CreateEffectPill(52, 26);
    private readonly Border _selection = new()
    {
        Height = 46,
        CornerRadius = new CornerRadius(23),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false
    };
    private readonly Grid _speedField = new()
    {
        Height = 52,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly List<Border> _speedLines = [];
    private readonly TextBlock[] _labels = new TextBlock[3];
    private readonly UISettings _ui = new();
    private readonly List<(UIElement Element, long Token)> _ancestors = [];
    private readonly Border _card;
    private AppWindow? _window;
    private bool _updating;
    private bool _tagOnly;
    private double _selectedPosition;
    private double _lastPosition;
    private double _segmentWidth;

    public event Action<ScanPerformanceMode>? ModeChanged;
    public ScanPerformanceMode Mode => (ScanPerformanceMode)(int)Math.Round(_input.Value);

    public ScanModeSelector()
    {
        // The card remains 380px wide. Extra transparent gutters belong to the
        // control so Composition effects can breathe past the rounded card edge.
        Width = 452;
        MaxWidth = 452;
        HorizontalAlignment = HorizontalAlignment.Left;

        _track.Children.Add(_glowOuter);
        _track.Children.Add(_glowInner);
        _track.Children.Add(_fastGlow);
        _track.Children.Add(_trackSurface);
        _track.Children.Add(_qualityHaloOuter);
        _track.Children.Add(_qualityHaloInner);
        _track.Children.Add(_selection);

        BuildSpeedLines();
        _track.Children.Add(_speedField);
        _track.Children.Add(BuildSegmentLabels());
        _track.Children.Add(_input);

        var layout = new Grid
        {
            Width = 348,
            Margin = new Thickness(0, 16, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_title, 0);
        Grid.SetRow(_track, 2);
        Grid.SetRow(_hint, 4);
        layout.Children.Add(_title);
        layout.Children.Add(_track);
        layout.Children.Add(_hint);

        _card = new Border
        {
            Width = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(28),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
            Background = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128))
        };
        var root = new Grid();
        root.Children.Add(_card);
        root.Children.Add(layout);
        Content = root;

        foreach (var element in EnumerateTranslatedElements())
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        _input.ValueChanged += (_, _) =>
        {
            UpdateAppearance(true);
            if (!_updating)
                ModeChanged?.Invoke(Mode);
        };
        _track.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(TrackPointerPressed), true);
        _track.SizeChanged += (_, _) => UpdateAppearance(false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMotion());
    }

    public void SetMode(ScanPerformanceMode mode, bool tagOnly)
    {
        if (!Enum.IsDefined(mode))
            mode = ScanPerformanceMode.Balanced;
        if (mode == Mode && tagOnly == _tagOnly)
            return;

        _updating = true;
        _tagOnly = tagOnly;
        _input.Value = (int)mode;
        _updating = false;
        UpdateAppearance(false);
    }

    private static Border CreateEffectPill(double height, double radius) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(radius),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private void BuildSpeedLines()
    {
        var definitions = new (double Width, double Top)[]
        {
            (27, 12),
            (17, 25),
            (35, 38)
        };
        foreach (var (width, top) in definitions)
        {
            var line = new Border
            {
                Width = width,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, top, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(235, 219, 255, 237)),
                IsHitTestVisible = false
            };
            ElementCompositionPreview.SetIsTranslationEnabled(line, true);
            _speedLines.Add(line);
            _speedField.Children.Add(line);
        }
    }

    private Grid BuildSegmentLabels()
    {
        var labels = new Grid { IsHitTestVisible = false };
        for (var index = 0; index < 3; index++)
        {
            labels.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = ModeNames[index],
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _labels[index] = label;
            Grid.SetColumn(label, index);
            labels.Children.Add(label);

            if (index == 0)
                continue;
            var divider = new Border
            {
                Width = 1,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
                IsHitTestVisible = false
            };
            Grid.SetColumn(divider, index);
            labels.Children.Add(divider);
        }
        return labels;
    }

    private IEnumerable<UIElement> EnumerateTranslatedElements()
    {
        yield return _selection;
        yield return _glowOuter;
        yield return _glowInner;
        yield return _fastGlow;
        yield return _qualityHaloOuter;
        yield return _qualityHaloInner;
        yield return _speedField;
    }

    private void TrackPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_track.ActualWidth <= 0)
            return;
        var x = args.GetCurrentPoint(_track).Position.X;
        var index = Math.Clamp((int)(x / _track.ActualWidth * 3), 0, 2);
        _input.Value = index;
        _input.Focus(FocusState.Pointer);
    }

    private void UpdateAppearance(bool animate)
    {
        var index = Math.Clamp((int)Mode, 0, 2);
        var color = ModeColors[index];
        _hint.Text = ModeDescriptions[index];
        AutomationProperties.SetName(_input,
            $"扫描模式，{ModeNames[index]}，{ModeDescriptions[index]}");
        AutomationProperties.SetHelpText(_input,
            "这是三段式选择器。点击一档，或使用左右方向键选择极速、均衡或质量；切换在下一次扫描生效。");
        AutomationProperties.SetItemStatus(_input, $"已选择{ModeNames[index]}");

        for (var labelIndex = 0; labelIndex < _labels.Length; labelIndex++)
        {
            var selected = labelIndex == index;
            _labels[labelIndex].Opacity = selected ? 1 : .58;
            if (selected)
                _labels[labelIndex].Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            else
                _labels[labelIndex].ClearValue(TextBlock.ForegroundProperty);
        }

        _selection.Background = CreateSelectionBrush(index);
        _selection.BorderBrush = new SolidColorBrush(WithAlpha(color, 205));
        _glowOuter.Background = new SolidColorBrush(WithAlpha(color, 255));
        _glowInner.Background = new SolidColorBrush(WithAlpha(color, 255));
        _fastGlow.Background = new SolidColorBrush(WithAlpha(color, 255));
        var haloBackground = new SolidColorBrush(WithAlpha(color, 40));
        var haloBorder = new SolidColorBrush(WithAlpha(color, 130));
        foreach (var halo in new[] { _qualityHaloOuter, _qualityHaloInner })
        {
            halo.Background = haloBackground;
            halo.BorderBrush = haloBorder;
            halo.BorderThickness = new Thickness(1);
        }

        _segmentWidth = _track.ActualWidth / 3;
        if (_segmentWidth <= 0)
        {
            UpdateMotion();
            return;
        }

        var selectionWidth = Math.Max(1, _segmentWidth - 8);
        _selectedPosition = index * _segmentWidth + 4;
        _selection.Width = selectionWidth;
        _fastGlow.Width = selectionWidth;
        _qualityHaloInner.Width = selectionWidth + 10;
        _qualityHaloOuter.Width = selectionWidth + 26;
        _glowInner.Width = selectionWidth + 44;
        _glowOuter.Width = selectionWidth + 72;
        _speedField.Width = _segmentWidth;
        _speedField.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _segmentWidth, _speedField.Height)
        };

        SetCenterPoint(_selection);
        SetCenterPoint(_fastGlow);
        SetCenterPoint(_glowOuter);
        SetCenterPoint(_glowInner);
        SetCenterPoint(_qualityHaloOuter);
        SetCenterPoint(_qualityHaloInner);

        SetTranslation(_fastGlow, _selectedPosition);
        SetTranslation(_glowOuter, _selectedPosition - 36);
        SetTranslation(_glowInner, _selectedPosition - 22);
        SetTranslation(_qualityHaloOuter, _selectedPosition - 13);
        SetTranslation(_qualityHaloInner, _selectedPosition - 5);
        SetTranslation(_speedField, index * _segmentWidth);

        var selectionVisual = ElementCompositionPreview.GetElementVisual(_selection);
        selectionVisual.StopAnimation("Translation.X");
        selectionVisual.Properties.InsertVector3("Translation",
            new Vector3((float)_selectedPosition, 0, 0));
        if (animate && CanAnimate())
        {
            var slide = selectionVisual.Compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(0, (float)_lastPosition);
            slide.InsertKeyFrame(1, (float)_selectedPosition);
            slide.Duration = TimeSpan.FromMilliseconds(index == 0 ? 150 : 230);
            selectionVisual.StartAnimation("Translation.X", slide);
        }
        _lastPosition = _selectedPosition;
        UpdateMotion();
    }

    private static Brush CreateSelectionBrush(int index)
    {
        var (start, end) = index switch
        {
            0 => (Color.FromArgb(225, 24, 139, 84), Color.FromArgb(238, 39, 205, 124)),
            2 => (Color.FromArgb(225, 115, 51, 190), Color.FromArgb(238, 190, 94, 237)),
            _ => (Color.FromArgb(225, 25, 105, 211), Color.FromArgb(238, 52, 160, 247))
        };
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, .5),
            EndPoint = new Windows.Foundation.Point(1, .5),
            GradientStops =
            {
                new GradientStop { Color = start, Offset = 0 },
                new GradientStop { Color = end, Offset = 1 }
            }
        };
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static void SetCenterPoint(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)(element.Width / 2),
            (float)(element.Height / 2), 0);
    }

    private static void SetTranslation(UIElement element, double x)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation.X");
        visual.Properties.InsertVector3("Translation", new Vector3((float)x, 0, 0));
    }

}
