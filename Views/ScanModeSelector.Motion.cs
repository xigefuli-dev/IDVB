using System.Numerics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private bool CanAnimate() => IsLoaded
        && Visibility == Visibility.Visible
        && _ui.AnimationsEnabled
        && _ancestors.All(item => item.Element.Visibility == Visibility.Visible)
        && _window is { IsVisible: true }
        && _window.Presenter is not OverlappedPresenter
            { State: OverlappedPresenterState.Minimized };

    private void UpdateMotion()
    {
        if (!CanAnimate()) FinishAppearanceTransition();
        UpdateDeepMotion();
        StopEffectAnimations();
        SetQualityEffectVisibility(Visibility.Collapsed);
        _fastGlow.Visibility = Visibility.Collapsed;
        _speedField.Visibility = Visibility.Collapsed;
        if (Mode == ScanPerformanceMode.DeepScan)
            return;

        // Balanced is intentionally still: the blue selection pill is the only
        // state indicator, with no ambient pulse competing for attention.
        if (Mode == ScanPerformanceMode.Balanced)
            return;

        if (Mode == ScanPerformanceMode.Fast)
        {
            _fastGlow.Visibility = Visibility.Visible;
            _speedField.Visibility = Visibility.Visible;
            var fastGlow = ElementCompositionPreview.GetElementVisual(_fastGlow);
            fastGlow.Opacity = .16f;

            if (!CanAnimate())
            {
                ShowStaticSpeedLines();
                return;
            }

            // One deliberately fast pulse, plus the independent speed lines.
            StartGlowPulse(fastGlow, 1f, 1.10f, .05f, .27f, 780);
            StartSpeedLines();
            return;
        }

        SetQualityEffectVisibility(Visibility.Visible);
        if (!CanAnimate())
        {
            ShowStaticQualityGlow();
            return;
        }

        StartQualityGlow();
    }

    private void StartQualityGlow()
    {
        // Four independently timed layers gradually drift out of phase. This
        // creates depth without reintroducing the directional echo animation.
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_glowOuter),
            .88f, 1.11f, .025f, .10f, 4800);
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_glowInner),
            .91f, 1.09f, .04f, .15f, 4200);
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_qualityHaloOuter),
            .94f, 1.07f, .055f, .20f, 3600);
        StartGlowPulse(ElementCompositionPreview.GetElementVisual(_qualityHaloInner),
            .97f, 1.045f, .08f, .25f, 3100);
    }

    private static void StartGlowPulse(Visual visual, float minimumScale,
        float maximumScale, float minimumOpacity, float maximumOpacity,
        int durationMilliseconds)
    {
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(minimumScale, minimumScale, 1));
        scale.InsertKeyFrame(.5f, new Vector3(maximumScale, maximumScale, 1));
        scale.InsertKeyFrame(1, new Vector3(minimumScale, minimumScale, 1));
        scale.Duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", scale);

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, minimumOpacity);
        opacity.InsertKeyFrame(.5f, maximumOpacity);
        opacity.InsertKeyFrame(1, minimumOpacity);
        opacity.Duration = scale.Duration;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", opacity);
    }

    private void StartSpeedLines()
    {
        for (var index = 0; index < _speedLines.Count; index++)
        {
            var line = _speedLines[index];
            var visual = ElementCompositionPreview.GetElementVisual(line);
            var travel = visual.Compositor.CreateScalarKeyFrameAnimation();
            travel.InsertKeyFrame(0, (float)(-line.Width - index * 11));
            travel.InsertKeyFrame(1, (float)(_segmentWidth + line.Width));
            travel.Duration = TimeSpan.FromMilliseconds(430 + index * 115);
            travel.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation.X", travel);

            var flash = visual.Compositor.CreateScalarKeyFrameAnimation();
            flash.InsertKeyFrame(0, 0);
            flash.InsertKeyFrame(.18f, .8f);
            flash.InsertKeyFrame(.68f, .62f);
            flash.InsertKeyFrame(1, 0);
            flash.Duration = travel.Duration;
            flash.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", flash);
        }
    }

    private void ShowStaticSpeedLines()
    {
        for (var index = 0; index < _speedLines.Count; index++)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_speedLines[index]);
            visual.Opacity = .42f;
            visual.Properties.InsertVector3("Translation",
                new Vector3(12 + index * 23, 0, 0));
        }
    }

    private void ShowStaticQualityGlow()
    {
        ElementCompositionPreview.GetElementVisual(_glowOuter).Opacity = .07f;
        ElementCompositionPreview.GetElementVisual(_glowInner).Opacity = .11f;
        ElementCompositionPreview.GetElementVisual(_qualityHaloOuter).Opacity = .15f;
        ElementCompositionPreview.GetElementVisual(_qualityHaloInner).Opacity = .20f;
    }

    private void SetQualityEffectVisibility(Visibility visibility)
    {
        _glowOuter.Visibility = visibility;
        _glowInner.Visibility = visibility;
        _qualityHaloOuter.Visibility = visibility;
        _qualityHaloInner.Visibility = visibility;
    }

    private void StopEffectAnimations()
    {
        foreach (var element in new UIElement[]
                 { _glowOuter, _glowInner, _fastGlow, _qualityHaloOuter, _qualityHaloInner })
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            visual.Scale = Vector3.One;
            visual.Opacity = 0;
        }
        foreach (var line in _speedLines)
        {
            var visual = ElementCompositionPreview.GetElementVisual(line);
            visual.StopAnimation("Translation.X");
            visual.StopAnimation("Opacity");
            visual.Opacity = 0;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        for (var parent = VisualTreeHelper.GetParent(this);
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement element)
            {
                _ancestors.Add((element,
                    element.RegisterPropertyChangedCallback(VisibilityProperty,
                        (_, _) => UpdateMotion())));
            }
        }
        _window = ((App)Application.Current).MainWindow.AppWindow;
        _window.Changed += WindowChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _ui.AnimationsEnabledChanged += AnimationsChanged;
        UpdateAppearance(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        foreach (var (element, token) in _ancestors)
            element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        _ancestors.Clear();
        if (_window is not null)
            _window.Changed -= WindowChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _ui.AnimationsEnabledChanged -= AnimationsChanged;
        _window = null;
        _dragPointer = null;
        _dragTrackX = null;
        _isPointerDragging = false;
        _pointerNeedsSettle = false;
        _expandedForGesture = false;
        _track.ReleasePointerCaptures();
        UpdateAppearance(false);
    }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(UpdateMotion);

    private void AnimationsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateMotion);
}
