using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenCvSharp;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Display;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using XamlWindow = Microsoft.UI.Xaml.Window;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public sealed record ManualGateSelectionResult(
    MapScreenRect MainGateBounds,
    MapScreenRect SideGateBounds);

/// <summary>Interactive frozen-game selector used only while F4 manual recognition is active.</summary>
public sealed partial class MapManualRecognitionWindow
{
    private const double MinimumPhysicalSelectionSize = 6d;
    private readonly CapturedGameFrame _frame;
    private readonly MapScreenRect _viewportBounds;
    private readonly TaskCompletionSource<ManualGateSelectionResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _root = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 8, 12, 18)),
        IsTabStop = true
    };
    private readonly Canvas _canvas = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
    };
    private readonly TextBlock _instruction = new()
    {
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly List<Rect> _selections = [];
    private XamlWindow? _window;
    private Point? _dragStart;
    private Rect? _activeSelection;
    private bool _completed;
    private readonly ICaptureProtectionService? _captureProtection;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;

    private MapManualRecognitionWindow(
        CapturedGameFrame frame,
        MapScreenRect viewportBounds,
        ICaptureProtectionService? captureProtection)
    {
        _frame = frame;
        _viewportBounds = viewportBounds;
        _captureProtection = captureProtection;
    }

    public static async Task<ManualGateSelectionResult?> ShowAsync(
        CapturedGameFrame frame,
        MapScreenRect viewportBounds,
        CancellationToken cancellationToken,
        ICaptureProtectionService? captureProtection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selector = new MapManualRecognitionWindow(frame, viewportBounds, captureProtection);
        return await selector.ShowCoreAsync(cancellationToken);
    }

    private async Task<ManualGateSelectionResult?> ShowCoreAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var image = new Image
        {
            Source = await CreateBitmapAsync(_frame.Image),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        cancellationToken.ThrowIfCancellationRequested();
        _root.Children.Add(image);
        _root.Children.Add(_canvas);
        var instructions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 18, 24)),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(7),
            MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            Child = _instruction
        };
        _root.Children.Add(instructions);
        _root.Loaded += (_, _) =>
        {
            _root.Focus(FocusState.Programmatic);
            Render();
        };
        _root.SizeChanged += (_, _) => Render();
        _root.KeyDown += Root_KeyDown;
        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerCanceled += Canvas_PointerCanceled;

        _window = new XamlWindow
        {
            Content = _root,
            ExtendsContentIntoTitleBar = true,
            SystemBackdrop = new TransparentBackdrop()
        };
        _window.Closed += (_, _) => Complete(null, closeWindow: false);
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        _window.AppWindow.MoveAndResize(ToRectInt32(_frame.ClientBounds));
        _window.Activate();
        RegisterCaptureProtection();
        using var cancellationRegistration = cancellationToken.Register(
            () => CompleteOnDispatcher(dispatcher));
        try
        {
            return await _completion.Task;
        }
        finally
        {
            _captureProtectionRegistration?.Dispose();
            _captureProtectionRegistration = null;
            // Complete() detaches the content before closing the WinUI window.
            // If the user closed the window directly, the Closed handler also
            // clears _window so this block never touches an already-closed
            // DesktopWindow object.
            _window = null;
            _root.Children.Clear();
            _canvas.Children.Clear();
        }
    }
}

/// <summary>In-game top-three chooser shown when manual geometry remains ambiguous.</summary>
public sealed partial class MapManualCandidateWindow
{
    private readonly CapturedGameFrame _frame;
    private readonly IReadOnlyList<MapRecognitionChoice> _choices;
    private readonly string _reason;
    private readonly MapRepository _repository;
    private readonly MapScreenRect _recognitionBounds;
    private readonly IReadOnlyList<ImageSource?>? _preloadedChoicePreviews;
    private readonly CandidateLivePreviewAssets? _preloadedLivePreview;
    private readonly ICaptureProtectionService? _captureProtection;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;
    private readonly TaskCompletionSource<MapCandidateDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private XamlWindow? _window;
    private bool _completed;

    private MapManualCandidateWindow(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        string reason,
        ICaptureProtectionService? captureProtection,
        MapRepository repository,
        MapScreenRect recognitionBounds,
        IReadOnlyList<ImageSource?>? preloadedChoicePreviews,
        CandidateLivePreviewAssets? preloadedLivePreview)
    {
        _frame = frame;
        _choices = choices;
        _reason = reason;
        _captureProtection = captureProtection;
        _repository = repository;
        _recognitionBounds = recognitionBounds;
        _preloadedChoicePreviews = preloadedChoicePreviews;
        _preloadedLivePreview = preloadedLivePreview;
    }

    public static async Task<MapCandidateDecision> ShowAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        string reason,
        CancellationToken cancellationToken,
        ICaptureProtectionService? captureProtection,
        MapRepository repository,
        MapScreenRect recognitionBounds,
        IReadOnlyList<ImageSource?>? preloadedChoicePreviews = null,
        CandidateLivePreviewAssets? preloadedLivePreview = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chooser = new MapManualCandidateWindow(
            frame,
            choices,
            reason,
            captureProtection,
            repository,
            recognitionBounds,
            preloadedChoicePreviews,
            preloadedLivePreview);
        return await chooser.ShowCoreAsync(cancellationToken);
    }

    private async Task<MapCandidateDecision> ShowCoreAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var displayArea = DisplayArea.Primary;
        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 6, 10, 16)),
            IsTabStop = true,
            RowSpacing = 8,
            ColumnSpacing = 8,
            Padding = new Thickness(24, 18, 24, 24)
        };
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(38, GridUnitType.Star)
        });
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(62, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = "请选择正确地图",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{_reason}\n可点击候选或按 1–{_choices.Count}；Esc 取消。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 210, 210))
        });
        Grid.SetRow(header, 0);
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);

        var livePreview = await CreateLivePreviewPanelAsync();
        Grid.SetRow(livePreview, 1);
        Grid.SetColumn(livePreview, 0);
        root.Children.Add(livePreview);

        var choiceGrid = new Grid
        {
            RowSpacing = 12,
            ColumnSpacing = 12,
            Margin = new Thickness(14, 14, 4, 4)
        };
        choiceGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        choiceGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        for (var i = 0; i < _choices.Count; i++)
        {
            var choiceIndex = i;
            var choice = _choices[i];
            var row = i / 2;
            while (choiceGrid.RowDefinitions.Count <= row)
            {
                choiceGrid.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(300)
                });
            }
            var cell = await CreateChoiceCellAsync(choice, i);
            var button = new Button
            {
                Content = cell,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(choice.IsReferenceOnly
                    ? Color.FromArgb(24, 255, 255, 255)
                    : Color.FromArgb(44, 255, 255, 255))
            };
            button.Click += (_, _) => Complete(MapCandidateDecision.SelectKnownMap(choiceIndex));
            Grid.SetRow(button, row);
            Grid.SetColumn(button, i % 2);
            choiceGrid.Children.Add(button);
        }

        if (_choices.Count == 0)
        {
            var emptyState = new TextBlock
            {
                Text = "未找到已记录地图",
                FontSize = 24,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 210, 210)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            choiceGrid.Children.Add(emptyState);
        }

        var listFrame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(4, 10, 0, 0),
            Child = new ScrollViewer
            {
                Content = choiceGrid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        Grid.SetRow(listFrame, 1);
        Grid.SetColumn(listFrame, 1);
        root.Children.Add(listFrame);

        var surveyButton = new Button
        {
            Content = "没有我想要的地图",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(16, 9, 16, 9),
            CornerRadius = new CornerRadius(8)
        };
        surveyButton.Click += (_, _) => Complete(MapCandidateDecision.StartSurvey());
        Grid.SetRow(surveyButton, 0);
        Grid.SetColumnSpan(surveyButton, 2);
        Canvas.SetZIndex(surveyButton, 100);
        root.Children.Add(surveyButton);

        root.Loaded += (_, _) => root.Focus(FocusState.Programmatic);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Complete(MapCandidateDecision.Cancel());
                return;
            }
            var index = e.Key switch
            {
                VirtualKey.Number1 or VirtualKey.NumberPad1 => 0,
                VirtualKey.Number2 or VirtualKey.NumberPad2 => 1,
                VirtualKey.Number3 or VirtualKey.NumberPad3 => 2,
                VirtualKey.Number4 or VirtualKey.NumberPad4 => 3,
                VirtualKey.Number5 or VirtualKey.NumberPad5 => 4,
                VirtualKey.Number6 or VirtualKey.NumberPad6 => 5,
                VirtualKey.Number7 or VirtualKey.NumberPad7 => 6,
                VirtualKey.Number8 or VirtualKey.NumberPad8 => 7,
                VirtualKey.Number9 or VirtualKey.NumberPad9 => 8,
                _ => -1
            };
            if (index >= 0 && index < _choices.Count)
            {
                e.Handled = true;
                Complete(MapCandidateDecision.SelectKnownMap(index));
            }
        };

        _window = new XamlWindow
        {
            Content = root,
            ExtendsContentIntoTitleBar = true
        };
        _window.Closed += (_, _) => Complete(MapCandidateDecision.Cancel(), closeWindow: false);
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        _window.AppWindow.MoveAndResize(displayArea.OuterBounds);
        _window.Activate();
        RegisterCaptureProtection();
        // 消除 WinUI 默认白色底色：将窗口设为分层半透明
        var hwnd = WindowNative.GetWindowHandle(_window);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int LWA_ALPHA = 0x2;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        _ = SetLayeredWindowAttributes(hwnd, 0, 230, LWA_ALPHA);
        using var cancellationRegistration = cancellationToken.Register(
            () => CompleteOnDispatcher(dispatcher));
        try
        {
            return await _completion.Task;
        }
        finally
        {
            _captureProtectionRegistration?.Dispose();
            _captureProtectionRegistration = null;
            _window = null;
            root.Children.Clear();
        }
    }

    private void RegisterCaptureProtection()
    {
        if (_captureProtection is null || _window is null)
            return;
        try
        {
            _captureProtectionRegistration = _captureProtection.RegisterWindow(
                WindowNative.GetWindowHandle(_window),
                CaptureProtectionWindowCategory.DisplayLayer,
                "地图候选窗口");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[ManualCandidate] 捕获保护登记失败：{exception.Message}");
        }
    }

    private void Complete(
        MapCandidateDecision result,
        bool closeWindow = true,
        bool restoreGameFocus = true)
    {
        if (_completed)
            return;
        _completed = true;
        var window = _window;
        _window = null;
        if (closeWindow && window is not null)
        {
            window.Content = null;
            window.Close();
        }
        _completion.TrySetResult(result);
        if (restoreGameFocus)
            SetForegroundWindow(_frame.WindowHandle);
    }

    private void CompleteOnDispatcher(DispatcherQueue dispatcher)
    {
        if (dispatcher.HasThreadAccess)
        {
            Complete(MapCandidateDecision.Cancel(), restoreGameFocus: false);
            return;
        }
        dispatcher.TryEnqueue(
            () => Complete(MapCandidateDecision.Cancel(), restoreGameFocus: false));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}
/*
 * 文件职责：MapManualRecognitionWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
