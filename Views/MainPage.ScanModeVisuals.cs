using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private static readonly TimeSpan ScanAccentTransitionDuration =
        TimeSpan.FromMilliseconds(460);

    private readonly UISettings _scanVisualUiSettings = new();
    private HomePage? _scanVisualHomePage;
    private Color _scanAccentCurrent = ScanModeSelector.GetAccentColor(
        Features.Maps.ScanPerformanceMode.Balanced);
    private Color _scanAccentStart;
    private Color _scanAccentTarget;
    private long _scanAccentStartedAt;
    private bool _scanAccentAnimationRunning;

    private void InitializeScanModeVisuals()
    {
        _scanAccentStart = _scanAccentCurrent;
        _scanAccentTarget = _scanAccentCurrent;
        Unloaded += (_, _) => StopScanAccentAnimation();
    }

    private void ConnectScanVisuals(HomePage homePage)
    {
        DisconnectScanVisuals();
        _scanVisualHomePage = homePage;
        homePage.ScanModeVisualChanged += HomePage_ScanModeVisualChanged;
        StartScanAccentTransition(homePage.CurrentScanModeAccent, animate: false);
    }

    private void DisconnectScanVisuals()
    {
        if (_scanVisualHomePage is not null)
            _scanVisualHomePage.ScanModeVisualChanged -= HomePage_ScanModeVisualChanged;
        _scanVisualHomePage = null;
    }

    private void HomePage_ScanModeVisualChanged(Color color, bool animate) =>
        StartScanAccentTransition(color, animate);

    private void StartScanAccentTransition(Color target, bool animate)
    {
        var now = Stopwatch.GetTimestamp();
        if (_scanAccentAnimationRunning)
            UpdateScanAccentFrame(now);

        if (!animate || !_scanVisualUiSettings.AnimationsEnabled)
        {
            StopScanAccentAnimation();
            _scanAccentCurrent = target;
            _scanAccentStart = target;
            _scanAccentTarget = target;
            ApplyScanAccent(target);
            return;
        }

        if (_scanAccentCurrent.Equals(target))
            return;

        // Interruption begins at the currently rendered color, never at the
        // preceding mode's nominal color. Rapid clicks therefore preserve
        // visual continuity instead of restarting or snapping backward.
        _scanAccentStart = _scanAccentCurrent;
        _scanAccentTarget = target;
        _scanAccentStartedAt = now;
        if (_scanAccentAnimationRunning)
            return;

        _scanAccentAnimationRunning = true;
        CompositionTarget.Rendering += ScanAccentRendering;
    }

    private void ScanAccentRendering(object? sender, object args) =>
        UpdateScanAccentFrame(Stopwatch.GetTimestamp());

    private void UpdateScanAccentFrame(long now)
    {
        var elapsedSeconds = (now - _scanAccentStartedAt) /
            (double)Stopwatch.Frequency;
        var progress = Math.Clamp(
            elapsedSeconds / ScanAccentTransitionDuration.TotalSeconds, 0d, 1d);
        var eased = progress * progress * (3d - 2d * progress);
        _scanAccentCurrent = Interpolate(_scanAccentStart, _scanAccentTarget, eased);
        ApplyScanAccent(_scanAccentCurrent);
        if (progress >= 1d)
            StopScanAccentAnimation();
    }

    private void ApplyScanAccent(Color color)
    {
        _scanVisualHomePage?.SetAmbientAccent(color);
    }

    private void StopScanAccentAnimation()
    {
        if (!_scanAccentAnimationRunning)
            return;
        CompositionTarget.Rendering -= ScanAccentRendering;
        _scanAccentAnimationRunning = false;
    }

    private static Color Interpolate(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            Lerp(from.A, to.A, amount),
            Lerp(from.R, to.R, amount),
            Lerp(from.G, to.G, amount),
            Lerp(from.B, to.B, amount));
    }

    private static byte Lerp(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * amount);
}
