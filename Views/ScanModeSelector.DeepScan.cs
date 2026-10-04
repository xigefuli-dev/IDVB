using System.Numerics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private readonly DeepScanVisual _deepAtmosphere = CreateDeepPlate(DeepScanVisual.Layer.Atmosphere);
    private readonly DeepScanVisual _deepWisps = CreateDeepPlate(DeepScanVisual.Layer.Wisps);
    private readonly DeepScanVisual _deepDust = CreateDeepPlate(DeepScanVisual.Layer.Dust);
    private readonly DeepScanVisual _deepShock = CreateDeepPlate(DeepScanVisual.Layer.Shock);
    private readonly DeepScanLattice _deepTrack = new() { Opacity = 0 };
    private readonly Canvas _deepLightHost = new()
    {
        Width = 0, Height = 0, IsHitTestVisible = false, Opacity = 0,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _deepCardSurface = new()
    {
        Width = 380, HorizontalAlignment = HorizontalAlignment.Center,
        CornerRadius = new CornerRadius(28), IsHitTestVisible = false, Opacity = 0,
        Background = DeepGradient((0xF21B102B, 0), (0xDD25113E, .65), (0xD03F205F, 1))
    };
    private const int _visibleSegments = 4;
    private uint? _dragPointer;
    private double? _dragTrackX;
    private double _dragGrabOffset;
    private double _pointerPressX;
    private bool _isPointerDragging;
    private bool _pointerNeedsSettle;
    private ScanPerformanceMode _lastAppearanceMode = ScanPerformanceMode.Balanced;
    private bool _deepMotionRunning;
    private bool _deepEffectsVisible;

    private static DeepScanVisual CreateDeepPlate(DeepScanVisual.Layer layer) => new(layer)
    {
        Width = 740, Height = 420,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private Canvas BuildDeepLightHost()
    {
        // Overflow must not participate in measuring the card.
        foreach (var light in new[] { _deepAtmosphere, _deepWisps, _deepDust, _deepShock })
        {
            Canvas.SetLeft(light, -light.Width / 2);
            Canvas.SetTop(light, -light.Height / 2);
            _deepLightHost.Children.Add(light);
        }
        return _deepLightHost;
    }

    private void SelectAtPointer(PointerRoutedEventArgs args)
    {
        var x = args.GetCurrentPoint(_track).Position.X;
        SelectAtTrackPosition(x);
    }

    private void SelectAtTrackPosition(double x)
    {
        _dragTrackX = x;
        UpdateGeometryPointer();
        if (_appearanceAnimating) AdvanceAppearance();
        // All four slots have fixed widths, including during a drag.
        _input.Value = ScanModeVisualRules.HitTest(x - _dragGrabOffset, _track.ActualWidth,
            _appearanceFrame.Expansion, _visibleSegments);
        RenderAppearanceFrame();
    }

    private void TrackPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId || _track.ActualWidth <= 0) return;
        if (!_isPointerDragging)
        {
            // A press selects with animation; small click jitter must not replace
            // that animation with a direct pointer pose.
            if (Math.Abs(args.GetCurrentPoint(_track).Position.X - _pointerPressX) < 3) return;
            _isPointerDragging = true;
        }
        SelectAtTrackPosition(args.GetCurrentPoint(_track).Position.X);
        args.Handled = true;
    }

    private void TrackPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId) return;
        if (_isPointerDragging && _track.ActualWidth > 0) SelectAtPointer(args);
        _dragPointer = null;
        _dragTrackX = null;
        _pointerNeedsSettle = _isPointerDragging;
        _isPointerDragging = false;
        _track.ReleasePointerCapture(args.Pointer);
        UpdateAppearance(true);
        CommitMode();
        args.Handled = true;
    }

    private void TrackPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragPointer.HasValue) return;
        if (_isPointerDragging && _dragTrackX.HasValue && _track.ActualWidth > 0)
            SelectAtTrackPosition(_dragTrackX.Value);
        _dragPointer = null;
        _dragTrackX = null;
        _pointerNeedsSettle = _isPointerDragging;
        _isPointerDragging = false;
        UpdateAppearance(true);
        CommitMode();
    }

    private static LinearGradientBrush DeepGradient(params (uint Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, .2), EndPoint = new Windows.Foundation.Point(1, .8)
        };
        foreach (var (argb, offset) in stops)
            brush.GradientStops.Add(new GradientStop { Color = Argb(argb), Offset = offset });
        return brush;
    }

    private static Color Argb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void UpdateDeepMotion()
    {
        var active = AllowsGlass(FluentTheme.Snapshot(this))
            && (Mode == ScanPerformanceMode.DeepScan || _appearanceFrame.Deep > .001);
        var moving = active && CanAnimate();
        if (_deepMotionRunning == moving && _deepEffectsVisible == active) return;
        _deepMotionRunning = moving;
        _deepEffectsVisible = active;
        _deepTrack.SetRunning(moving);
        foreach (var element in new FrameworkElement[] { _deepAtmosphere, _deepWisps, _deepDust, _deepShock, _card })
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("RotationAngleInDegrees");
            visual.Scale = Vector3.One;
            visual.RotationAngleInDegrees = 0;
            visual.Opacity = element == _deepShock ? 0 : 1;
        }
        if (!moving) return;
        foreach (var plate in new[] { _deepAtmosphere, _deepWisps, _deepDust, _deepShock }) SetCenterPoint(plate);
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_deepAtmosphere), .96f, 1.045f, .78f, 1f, 6100);
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_deepWisps), .94f, 1.06f, .65f, 1f, 4300);
        DriftPlate(_deepWisps, -4, 5, 17000);
        DriftPlate(_deepDust, 3, -4, 23000);
    }

    private static void DriftPlate(UIElement plate, float from, float to, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(plate);
        using var drift = visual.Compositor.CreateScalarKeyFrameAnimation();
        drift.InsertKeyFrame(0, from);
        drift.InsertKeyFrame(.5f, to);
        drift.InsertKeyFrame(1, from);
        drift.Duration = TimeSpan.FromMilliseconds(milliseconds);
        drift.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngleInDegrees", drift);
    }

    private void StopDeepImpact()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_deepShock);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;
    }

    private void PlayDeepEntry()
    {
        var shock = ElementCompositionPreview.GetElementVisual(_deepShock);
        using var expansion = shock.Compositor.CreateVector3KeyFrameAnimation();
        expansion.InsertKeyFrame(0, new Vector3(.28f, .28f, 1));
        expansion.InsertKeyFrame(.16f, new Vector3(.20f, .20f, 1));
        expansion.InsertKeyFrame(.52f, new Vector3(1.05f, 1.05f, 1));
        expansion.InsertKeyFrame(1, new Vector3(1.38f, 1.38f, 1));
        expansion.Duration = TimeSpan.FromMilliseconds(1450);
        shock.StartAnimation("Scale", expansion);
        using var flash = shock.Compositor.CreateScalarKeyFrameAnimation();
        flash.InsertKeyFrame(0, 0);
        flash.InsertKeyFrame(.16f, .35f);
        flash.InsertKeyFrame(.24f, 1);
        flash.InsertKeyFrame(.48f, .8f);
        flash.InsertKeyFrame(1, 0);
        flash.Duration = expansion.Duration;
        shock.StartAnimation("Opacity", flash);
    }
}

