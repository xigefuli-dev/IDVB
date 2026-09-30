using System.Diagnostics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private readonly record struct AppearanceFrame(double Expansion, double Deep, double Index, Color Accent);
    private AppearanceFrame _appearanceFrame = new(0, 0, 1, GetAccentColor(ScanPerformanceMode.Balanced));
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
    private readonly SolidColorBrush _effectFill = new(), _haloFill = new(), _haloStroke = new();

    private void BeginAppearanceTransition(bool animate)
    {
        var target = new AppearanceFrame(_visibleSegments == 4 ? 1 : 0,
            Mode == ScanPerformanceMode.DeepScan ? 1 : 0, (int)Mode, AccentColor);
        if (_appearanceReady && target == _appearanceTarget && !_pointerNeedsSettle)
        {
            // Layout notifications and pointer release must not restart or snap
            // an in-flight transition, especially while expanding under a drag.
            RenderAppearanceFrame();
            return;
        }
        if (_appearanceAnimating && !_pointerNeedsSettle) AdvanceAppearance();
        _selectionTransitionMilliseconds = _pointerNeedsSettle ? 100 : 180;
        _pointerNeedsSettle = false;
        _appearanceStart = _appearanceFrame;
        _appearanceTarget = target;
        var shouldAnimate = _appearanceReady && animate && CanAnimate();
        _appearanceReady = true;
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
        // A held pointer can already be inside the newly revealed slot even
        // when no new move event arrives during the expansion.
        if (_isPointerDragging && _dragPointer.HasValue && _dragTrackX.HasValue && _track.ActualWidth > 0)
            _input.Value = ScanModeVisualRules.HitTest(_dragTrackX.Value - _dragGrabOffset,
                _track.ActualWidth, _appearanceFrame.Expansion, _visibleSegments);
    }

    private void AdvanceAppearance()
    {
        var elapsed = Stopwatch.GetElapsedTime(_appearanceStarted).TotalMilliseconds;
        var pose = ScanModeVisualRules.ResponseProgress(elapsed, _selectionTransitionMilliseconds);
        var layout = ScanModeVisualRules.ResponseProgress(elapsed, 150);
        var surface = ScanModeVisualRules.ResponseProgress(elapsed, 180);
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
        var d = frame.Deep;
        _segmentLabels.ColumnDefinitions[3].Width = frame.Expansion <= 0
            ? new GridLength(0) : new GridLength(frame.Expansion, GridUnitType.Star);
        _labels[3].Visibility = frame.Expansion > .001 ? Visibility.Visible : Visibility.Collapsed;
        _dividers[2].Visibility = _labels[3].Visibility;
        _deepCardSurface.Opacity = d;
        _deepLightHost.Opacity = d;
        _deepTrack.Opacity = d;
        _trackSurface.Background = _railFill;
        _trackSurface.BorderBrush = _railStroke;
        _railFill.GradientStops[0].Color = MixColor(Argb(0x3E808080), Argb(0xFF070510), d);
        _railFill.GradientStops[1].Color = MixColor(Argb(0x3E808080), Argb(0xFF10091C), d);
        _railFill.GradientStops[2].Color = MixColor(Argb(0x3E808080), Argb(0xFF1D112D), d);
        _railStroke.GradientStops[0].Color = MixColor(Argb(0x2A808080), Argb(0x405B3585), d);
        _railStroke.GradientStops[1].Color = MixColor(Argb(0x2A808080), Argb(0xA0D1A3FF), d);
        _railStroke.GradientStops[2].Color = MixColor(Argb(0x2A808080), Argb(0xFFFFEAFF), d);
        _trackSurface.CornerRadius = new CornerRadius(Mix(29, 16, d));
        _selection.CornerRadius = new CornerRadius(Mix(23, 12, d));
        _selection.Background = _pillFill;
        _selection.BorderBrush = _pillStroke;
        _pillFill.GradientStops[0].Color = MixColor(Shade(frame.Accent, .66, 226), Argb(0xFFEEDAFF), d);
        _pillFill.GradientStops[1].Color = MixColor(Shade(frame.Accent, .84, 234), Argb(0xFFD6B7FF), d);
        _pillFill.GradientStops[2].Color = MixColor(Shade(frame.Accent, 1.03, 242), Argb(0xFFF9F0FF), d);
        _pillStroke.Color = MixColor(WithAlpha(frame.Accent, 205), Argb(0xFFFFF1FF), d);

        var text = ActualTheme == ElementTheme.Light ? Argb(0xFF202020) : Argb(0xFFF3F3F3);
        _title.Text = d >= .5 ? "DeepScan" : "扫描模式";
        _title.FontWeight = d >= .5 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        _title.CharacterSpacing = (int)(100 * d);
        _title.Opacity = Math.Abs(2 * d - 1);
        _titleBrush.Color = MixColor(text, Argb(0xFFF0DBFF), d);
        _title.Foreground = _titleBrush;
        _hintBrush.Color = MixColor(text, Argb(0xFFE0C5FA), d);
        _hint.Foreground = _hintBrush;
        for (var i = 0; i < _labels.Length; i++)
        {
            var weight = Math.Max(0, 1 - Math.Abs(i - frame.Index));
            var foreground = MixColor(text, Microsoft.UI.Colors.White, weight);
            _labelBrushes[i].Color = MixColor(foreground, i == 3 ? Argb(0xFF2C0F48) : Argb(0xFFDEC7F8), d);
            _labels[i].Foreground = _labelBrushes[i];
            _labels[i].Opacity = (.58 + .42 * weight) * (i == 3 ? frame.Expansion : 1);
        }
        _effectFill.Color = frame.Accent;
        _haloFill.Color = WithAlpha(frame.Accent, 40);
        _haloStroke.Color = WithAlpha(frame.Accent, 130);
        foreach (var effect in new[] { _glowOuter, _glowInner, _fastGlow }) effect.Background = _effectFill;
        foreach (var halo in new[] { _qualityHaloOuter, _qualityHaloInner })
        {
            halo.Background = _haloFill;
            halo.BorderBrush = _haloStroke;
            halo.BorderThickness = new Thickness(1);
        }
        RenderSelectionGeometry(frame);
        UpdateDeepMotion();
    }

    private void RenderSelectionGeometry(AppearanceFrame frame)
    {
        if (_track.ActualWidth <= 0) return;
        _segmentWidth = _track.ActualWidth / (3 + frame.Expansion);
        var width = Math.Max(1, _segmentWidth - 8);
        _selectedPosition = Math.Clamp(frame.Index, 0, 2 + frame.Expansion) * _segmentWidth + 4;
        _deepTrack.SetFillBoundary(_selectedPosition);
        _selection.Width = _fastGlow.Width = width;
        _qualityHaloInner.Width = width + 10;
        _qualityHaloOuter.Width = width + 26;
        _glowInner.Width = width + 44;
        _glowOuter.Width = width + 72;
        _speedField.Width = _segmentWidth;
        _speedField.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _segmentWidth, _speedField.Height)
        };
        foreach (var element in new[] { _selection, _fastGlow, _glowOuter, _glowInner, _qualityHaloOuter, _qualityHaloInner })
            SetCenterPoint(element);
        SetTranslation(_selection, _selectedPosition);
        SetTranslation(_fastGlow, _selectedPosition);
        SetTranslation(_glowOuter, _selectedPosition - 36);
        SetTranslation(_glowInner, _selectedPosition - 22);
        SetTranslation(_qualityHaloOuter, _selectedPosition - 13);
        SetTranslation(_qualityHaloInner, _selectedPosition - 5);
        SetTranslation(_speedField, _selectedPosition - 4);
    }

    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static Color MixColor(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(Mix(a.A, b.A, t)), (byte)Math.Round(Mix(a.R, b.R, t)),
        (byte)Math.Round(Mix(a.G, b.G, t)), (byte)Math.Round(Mix(a.B, b.B, t)));
}
