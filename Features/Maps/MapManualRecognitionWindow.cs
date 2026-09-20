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

