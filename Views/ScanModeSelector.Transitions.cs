using System.Diagnostics;
using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private readonly record struct AppearanceFrame(double Expansion, double Deep, double Index, Color Accent);
    private AppearanceFrame _appearanceFrame = new(1, 0, 1, GetAccentColor(ScanPerformanceMode.Balanced));
    private AppearanceFrame _appearanceStart, _appearanceTarget;
    private bool _appearanceReady, _appearanceAnimating;
    private long _appearanceStarted;
    private double _selectionTransitionMilliseconds = 180;
    private readonly LinearGradientBrush _railFill = DeepGradient((0x3E808080, 0), (0x3E808080, .5), (0x3E808080, 1));
    private readonly LinearGradientBrush _railStroke = DeepGradient((0x2A808080, 0), (0x2A808080, .5), (0x2A808080, 1));
    private readonly LinearGradientBrush _pillFill = DeepGradient((0x00000000, 0), (0x00000000, .5), (0x00000000, 1));
    private readonly SolidColorBrush _pillStroke = new();
    private readonly SolidColorBrush[] _labelBrushes = [new(), new(), new(), new()];
    private readonly SolidColorBrush _titleBrush = new(), _hintBrush = new();
    private readonly SolidColorBrush _haloFill = new(), _haloStroke = new();
    private AppearanceFrame? _renderedFrame;
    private ThemeSnapshot? _renderedTheme;
    private readonly Color[] _selectionStartColors = new Color[3];
    private readonly Color[] _selectionTargetColors = new Color[3];
    private double _surfaceProgress = 1;

    private void BeginAppearanceTransition(bool animate)
    {
        var target = new AppearanceFrame(1,
            Mode == ScanPerformanceMode.DeepScan ? 1 : 0, (int)Mode, AccentColor);
        if (_appearanceReady && target == _appearanceTarget && !_pointerNeedsSettle
            && _renderedTheme is { } rendered && SameSelectorSurface(rendered, FluentTheme.Snapshot(this)))
        {
            // Layout notifications and pointer release must not restart or snap
            // an in-flight transition.
            RenderAppearanceFrame();
            return;
        }
        if (_appearanceAnimating && !_pointerNeedsSettle) AdvanceAppearance();
        _selectionTransitionMilliseconds = _pointerNeedsSettle ? 100 : 180;
        _pointerNeedsSettle = false;
        _appearanceStart = _appearanceFrame;
        _appearanceTarget = target;
        // Contrast correction belongs to the endpoints, not every displayed frame.
        for (var i = 0; i < 3; i++)
        {
            _selectionStartColors[i] = _pillFill.GradientStops[i].Color;
            var shade = i == 0 ? .66 : i == 1 ? .84 : 1.03;
            var alpha = i == 0 ? (byte)226 : i == 1 ? (byte)234 : (byte)242;
            var deepColor = i == 0 ? Argb(0xFFEEDAFF) : i == 1 ? Argb(0xFFD6B7FF) : Argb(0xFFF9F0FF);
            var deep = AllowsGlass(FluentTheme.Snapshot(this)) ? target.Deep : 0;
            _selectionTargetColors[i] = MixColor(SelectionStop(target.Accent, shade, alpha), deepColor, deep);
        }
        var shouldAnimate = _appearanceReady && animate && CanAnimate();
        _appearanceReady = true;
        TransitionGeometry(shouldAnimate);
        if (!shouldAnimate)
        {
            FinishAppearanceTransition();
            return;
        }
        _appearanceStarted = Stopwatch.GetTimestamp();
        if (!_appearanceAnimating)
        {
            _appearanceAnimating = true;
            CompositionTarget.Rendering += AppearanceRendering;
        }
    }

    private void AppearanceRendering(object? sender, object args)
    {
        AdvanceAppearance();
    }

    private void AdvanceAppearance()
    {
        var elapsed = Stopwatch.GetElapsedTime(_appearanceStarted).TotalMilliseconds;
        var pose = ScanModeVisualRules.ResponseProgress(elapsed, _selectionTransitionMilliseconds);
        var layout = ScanModeVisualRules.ResponseProgress(elapsed, 150);
        var surface = ScanModeVisualRules.ResponseProgress(elapsed, 180);
        _surfaceProgress = surface;
        _appearanceFrame = new(
            Mix(_appearanceStart.Expansion, _appearanceTarget.Expansion, layout),
            Mix(_appearanceStart.Deep, _appearanceTarget.Deep, surface),
            Mix(_appearanceStart.Index, _appearanceTarget.Index, pose),
            MixColor(_appearanceStart.Accent, _appearanceTarget.Accent, surface));
        RenderAppearanceFrame();
        if (elapsed >= 180) StopAppearanceRendering();
    }

    private void StopAppearanceRendering()
    {
        if (!_appearanceAnimating) return;
        _appearanceAnimating = false;
        CompositionTarget.Rendering -= AppearanceRendering;
    }

    private void FinishAppearanceTransition()
    {
        StopAppearanceRendering();
        if (!_appearanceReady) return;
        _appearanceFrame = _appearanceTarget;
        _surfaceProgress = 1;
        TransitionGeometry(false);
        RenderAppearanceFrame();
    }

    private void RenderAppearanceFrame()
    {
        if (_isPointerDragging && _dragPointer.HasValue && _dragTrackX.HasValue && _track.ActualWidth > 0)
            _appearanceFrame = _appearanceFrame with
            {
                Index = ScanModeVisualRules.PointerIndex(_dragTrackX.Value - _dragGrabOffset,
                    _track.ActualWidth, _appearanceFrame.Expansion, _visibleSegments)
            };
        var frame = _appearanceFrame;
        var theme = FluentTheme.Snapshot(this);
        if (_renderedFrame == frame && ReferenceEquals(_renderedTheme, theme))
        {
            RenderSelectionGeometry(frame);
            return;
        }
        var themeChanged = _renderedTheme is null || !SameSelectorSurface(_renderedTheme, theme);
        var surfaceChanged = !_renderedFrame.HasValue
            || _renderedFrame.Value.Deep != frame.Deep
            || !_renderedFrame.Value.Accent.Equals(frame.Accent)
            || themeChanged;
        if (!_renderedFrame.HasValue || _renderedFrame.Value.Expansion != frame.Expansion)
        {
            _dividers[2].Opacity = frame.Expansion;
        }
        var text = ThemeResources.ToColor(theme[ThemeToken.Text]);
        var secondaryText = ThemeResources.ToColor(theme[ThemeToken.TextSecondary]);
        var selectionText = theme.IsHighContrast
            ? ThemeResources.ToColor(theme[ThemeToken.SelectionText]) : Microsoft.UI.Colors.White;
        var railFill = ThemeResources.ToColor(theme[ThemeToken.ControlFill]);
        var railStroke = ThemeResources.ToColor(theme[ThemeToken.ControlBorder]);
        var d = AllowsGlass(theme) ? frame.Deep : 0;
        if (surfaceChanged)
        {
            _deepCardSurface.Opacity = d;
            _deepLightHost.Opacity = d;
            _deepTrack.Opacity = d;
            _trackSurface.Background = _railFill;
            _trackSurface.BorderBrush = _railStroke;
            _railFill.GradientStops[0].Color = MixColor(railFill, Argb(0xFF070510), d);
            _railFill.GradientStops[1].Color = MixColor(railFill, Argb(0xFF10091C), d);
            _railFill.GradientStops[2].Color = MixColor(railFill, Argb(0xFF1D112D), d);
            _railStroke.GradientStops[0].Color = MixColor(railStroke, Argb(0x405B3585), d);
            _railStroke.GradientStops[1].Color = MixColor(railStroke, Argb(0xA0D1A3FF), d);
            _railStroke.GradientStops[2].Color = MixColor(railStroke, Argb(0xFFFFEAFF), d);
            _trackSurface.CornerRadius = new CornerRadius(Mix(29, 16, d));
            for (var i = 0; i < 3; i++)
                _pillFill.GradientStops[i].Color = MixColor(_selectionStartColors[i], _selectionTargetColors[i], _surfaceProgress);
            _pillStroke.Color = MixColor(WithAlpha(frame.Accent, 205), Argb(0xFFFFF1FF), d);

            if (theme.IsHighContrast)
            {
                foreach (var stop in _pillFill.GradientStops)
                    stop.Color = ThemeResources.ToColor(theme[ThemeToken.Selection]);
                _pillStroke.Color = ThemeResources.ToColor(theme[ThemeToken.SelectionBorder]);
            }
            _title.Text = d >= .5 ? "DeepScan" : "扫描模式";
            _title.FontWeight = d >= .5 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            // Text metrics change once at the semantic switch, never per frame.
            _title.CharacterSpacing = d >= .5 ? 100 : 0;
            _title.Opacity = Math.Abs(2 * d - 1);
            _titleBrush.Color = MixColor(text, Argb(0xFFF0DBFF), d);
            _title.Foreground = _titleBrush;
            _hintBrush.Color = MixColor(secondaryText, Argb(0xFFE0C5FA), d);
            _hint.Foreground = _hintBrush;
        }
        for (var i = 0; i < _labels.Length; i++)
        {
            var weight = Math.Max(0, 1 - Math.Abs(i - frame.Index));
            var foreground = MixColor(text, selectionText, weight);
            _labelBrushes[i].Color = MixColor(foreground, i == 3 ? Argb(0xFF2C0F48) : Argb(0xFFDEC7F8), d);
            _labels[i].Foreground = _labelBrushes[i];
            _labels[i].Opacity = (.58 + .42 * weight) * (i == 3 ? frame.Expansion : 1);
        }
        if (surfaceChanged)
        {
            _haloFill.Color = WithAlpha(frame.Accent, 40);
            _haloStroke.Color = WithAlpha(frame.Accent, 130);
            RenderGeometrySurface(frame with { Deep = d });
        }
        _renderedFrame = frame;
        _renderedTheme = theme;
        RenderSelectionGeometry(frame);
        UpdateDeepMotion();
    }

    private void RenderSelectionGeometry(AppearanceFrame frame)
    {
        if (_track.ActualWidth <= 0) return;
        _segmentWidth = _track.ActualWidth / (3 + frame.Expansion);
        _selectedPosition = Math.Clamp(frame.Index, 0, 2 + frame.Expansion) * _segmentWidth + 4;
        EnsureGeometry();
        UpdateGeometryPointer();
        if (_geometryTrackWidth != _track.ActualWidth)
        {
            _geometryTrackWidth = _track.ActualWidth;
            _geometry!.InsertScalar("Width", (float)_geometryTrackWidth);
            _speedField.Width = _geometryTrackWidth;
        }
    }

    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static bool SameSelectorSurface(ThemeSnapshot a, ThemeSnapshot b) =>
        a.IsDark == b.IsDark && a.IsHighContrast == b.IsHighContrast
        && a.FallbackReason == b.FallbackReason && a[ThemeToken.Text] == b[ThemeToken.Text]
        && a[ThemeToken.TextSecondary] == b[ThemeToken.TextSecondary]
        && a[ThemeToken.ControlFill] == b[ThemeToken.ControlFill]
        && a[ThemeToken.ControlBorder] == b[ThemeToken.ControlBorder]
        && (!b.IsHighContrast || (a[ThemeToken.Selection] == b[ThemeToken.Selection]
            && a[ThemeToken.SelectionText] == b[ThemeToken.SelectionText]
            && a[ThemeToken.SelectionBorder] == b[ThemeToken.SelectionBorder]));
    private static Color MixColor(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(Mix(a.A, b.A, t)), (byte)Math.Round(Mix(a.R, b.R, t)),
        (byte)Math.Round(Mix(a.G, b.G, t)), (byte)Math.Round(Mix(a.B, b.B, t)));
}
