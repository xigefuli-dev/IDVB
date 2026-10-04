using System.Numerics;
using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
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
/// Four persistent choices. The native slider owns keyboard input and accessibility;
/// the surrounding layers provide the segmented visual treatment and mode-specific motion.
/// </summary>
public sealed partial class ScanModeSelector : UserControl
{
    private static readonly string[] ModeNames = ["极速", "均衡", "质量", "DeepScan"];
    private static readonly string[] ModeDescriptions =
    [
        "更快的响应速度",
        "兼顾响应速度与扫描质量",
        "更细致的扫描结果",
        "DeepScan 已开启"
    ];
    private static readonly Color[] ModeColors =
    [
        Color.FromArgb(255, 50, 218, 137),
        Color.FromArgb(255, 48, 151, 255),
        Color.FromArgb(255, 182, 91, 242),
        Color.FromArgb(255, 208, 153, 255)
    ];

    private readonly Slider _input = new()
    {
        Minimum = 0,
        Maximum = 3,
        StepFrequency = 1,
        TickFrequency = 1,
        Value = 1,
        Opacity = 0,
        IsHitTestVisible = false,
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
    private readonly Border _fastGlow = CreateEffectPill(50, 25);
    private readonly Border _qualityHaloOuter = CreateEffectPill(62, 31);
    private readonly Border _qualityHaloInner = CreateEffectPill(52, 26);
    private readonly Border _selection = new()
    {
        Height = 50,
        CornerRadius = new CornerRadius(25),
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
    private readonly TextBlock[] _labels = new TextBlock[4];
    private readonly Grid _segmentLabels = new() { IsHitTestVisible = false };
    private readonly Border[] _dividers = new Border[3];
    private readonly UISettings _ui = new();
    private readonly List<(UIElement Element, long Token)> _ancestors = [];
    private readonly Border _card;
    private AppWindow? _window;
    private bool _updating;
    private bool _tagOnly;
    private double _selectedPosition;
    private double _segmentWidth;

    public event Action<ScanPerformanceMode>? ModeChanged;
    public event Action<ScanPerformanceMode>? ModePreviewChanged;
    private ScanPerformanceMode _committedMode = ScanPerformanceMode.Balanced;
    public bool IsInteracting => _dragPointer.HasValue;
    public ScanPerformanceMode Mode => (ScanPerformanceMode)(int)Math.Round(_input.Value);
    public Color AccentColor => GetAccentColor(Mode);

    public static Color GetAccentColor(ScanPerformanceMode mode)
    {
        var index = Enum.IsDefined(mode) ? (int)mode : (int)ScanPerformanceMode.Balanced;
        return ModeColors[index];
    }

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
        _track.Children.Add(_deepTrack);
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
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(105, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(34, 255, 255, 255), Offset = .48 },
                    new GradientStop { Color = Color.FromArgb(54, 0, 0, 0), Offset = 1 }
                }
            },
            Background = CreateGlassBrush()
        };
        var root = new Grid();
        root.Children.Add(BuildDeepLightHost());
        root.Children.Add(_card);
        root.Children.Add(_deepCardSurface);
        root.Children.Add(_sheen);
        root.Children.Add(layout);
        Content = root;
        FluentTheme.Observe(this, theme =>
        {
            if (NeedsAppearanceRefresh(theme)) UpdateAppearance(false);
        });

        foreach (var element in EnumerateTranslatedElements())
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        _input.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            UpdateAppearance(true);
            ModePreviewChanged?.Invoke(Mode);
            if (!IsInteracting) CommitMode();
        };
        _track.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(TrackPointerPressed), true);
        _track.PointerMoved += TrackPointerMoved;
        _track.PointerReleased += TrackPointerReleased;
        _track.PointerCaptureLost += TrackPointerCaptureLost;
        _track.SizeChanged += (_, _) => UpdateAppearance(false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => { if (_appearanceReady) RenderAppearanceFrame(); };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMotion());
    }

    private static AcrylicBrush CreateGlassBrush() => new()
    {
        TintColor = Color.FromArgb(255, 48, 50, 54),
        TintOpacity = .46,
        TintLuminosityOpacity = .16,
        FallbackColor = Color.FromArgb(245, 45, 46, 49)
    };

    public void SetMode(ScanPerformanceMode mode, bool tagOnly)
    {
        if (!Enum.IsDefined(mode))
            mode = ScanPerformanceMode.Balanced;
        _committedMode = mode;
        if (mode == Mode && tagOnly == _tagOnly)
            return;

        _updating = true;
        _tagOnly = tagOnly;
        if (mode != Mode)
        {
            // A settings rollback supersedes the current pointer gesture.
            _dragPointer = null;
            _dragTrackX = null;
            _isPointerDragging = false;
            _pointerNeedsSettle = false;
            _track.ReleasePointerCaptures();
        }
        _input.Value = (int)mode;
        _updating = false;
        UpdateAppearance(false);
    }

    private void CommitMode()
    {
        if (_committedMode == Mode) return;
        _committedMode = Mode;
        ModeChanged?.Invoke(Mode);
    }

    internal void SetCommittedMode(ScanPerformanceMode mode) => _committedMode = mode;

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
        var labels = _segmentLabels;
        for (var index = 0; index < ModeNames.Length; index++)
        {
            var label = new TextBlock
            {
                Text = ModeNames[index],
                FontSize = index == 3 ? 12 : 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _labels[index] = label;
            ElementCompositionPreview.SetIsTranslationEnabled(label, true);
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
            _dividers[index - 1] = divider;
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
        if (_track.ActualWidth <= 0 || !args.GetCurrentPoint(_track).Properties.IsLeftButtonPressed)
            return;
        _dragPointer = args.Pointer.PointerId;
        var pointerX = args.GetCurrentPoint(_track).Position.X;
        _pointerPressX = pointerX;
        _isPointerDragging = false;
        if (_appearanceAnimating) AdvanceAppearance();
        var selectionWidth = Math.Max(1, _segmentWidth - 8);
        _dragGrabOffset = pointerX >= _selectedPosition && pointerX <= _selectedPosition + selectionWidth
            ? pointerX - (_selectedPosition + selectionWidth / 2) : 0;
        _track.CapturePointer(args.Pointer);
        SelectAtPointer(args);
        _input.Focus(FocusState.Pointer);
        args.Handled = true;
    }

    private void UpdateAppearance(bool animate)
    {
        UpdateGlassSurface(FluentTheme.Snapshot(this));
        var index = Math.Clamp((int)Mode, 0, 3);
        var enterDeepScan = animate && index == 3 && _lastAppearanceMode != ScanPerformanceMode.DeepScan;
        _lastAppearanceMode = Mode;
        _hint.Text = ModeDescriptions[index];
        AutomationProperties.SetName(_input,
            $"扫描模式，{ModeNames[index]}，{ModeDescriptions[index]}");
        AutomationProperties.SetHelpText(_input,
            "四段式选择器：极速、均衡、质量、DeepScan。使用左右方向键选择；切换在下一次扫描生效。");
        AutomationProperties.SetItemStatus(_input, $"已选择{ModeNames[index]}");

        BeginAppearanceTransition(animate);
        UpdateMotion();
        if (Mode != ScanPerformanceMode.DeepScan) StopDeepImpact();
        if (enterDeepScan && CanAnimate()) PlayDeepEntry();
    }
    private static Color Shade(Color color, double amount, byte alpha) =>
        Color.FromArgb(
            alpha,
            (byte)Math.Clamp(Math.Round(color.R * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * amount), 0, 255));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static void SetCenterPoint(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)(element.Width / 2),
            (float)(element.Height / 2), 0);
    }

}
