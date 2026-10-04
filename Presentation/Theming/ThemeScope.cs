using IDVBuff.Appearance;
using IDVBuff.Diagnostics;
using Microsoft.UI.Xaml;

namespace IDVBuff.Presentation.Theming;

internal sealed class ThemeScope : IDisposable
{
    private readonly ElementTheme _previousTheme;
    private Window? _window;
    private bool _useBackdrop;
    private bool _backdropFailed;
    private ThemeWindowMonitor? _monitor;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _windowBrush = new();
    public FrameworkElement Root { get; }
    public ThemeProfile Profile { get; }
    public ThemeResources Resources { get; }
    public bool IsDisposed { get; private set; }
    public Microsoft.UI.Xaml.Media.Brush WindowBrush => _windowBrush;

    internal ThemeScope(FrameworkElement root, ThemeProfile profile)
    {
        Root = root;
        Profile = profile;
        _previousTheme = root.RequestedTheme;
        Resources = new(ThemeService.For(root).Snapshot);
        root.Resources.MergedDictionaries.Add(Resources.Dictionary);
    }

    internal void ConnectWindow(Window window, bool useBackdrop)
    {
        _window = window;
        _useBackdrop = useBackdrop;
        _monitor = new ThemeWindowMonitor(window);
        window.Closed += OnClosed;
        Apply(Resources.Snapshot, Resources.Revision);
    }

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    internal void Dispatch(Action action)
    {
        if (IsDisposed) return;
        if (Root.DispatcherQueue.HasThreadAccess) action();
        else Root.DispatcherQueue.TryEnqueue(() => { if (!IsDisposed) action(); });
    }

    internal void Apply(ThemeSnapshot snapshot, long revision, bool animateAccent = false)
    {
        if (IsDisposed || revision < Resources.Revision) return;
        if (_backdropFailed) snapshot = snapshot with { EffectiveMaterial = ThemeMaterial.Solid, FallbackReason = "BackdropFailure" };
        Resources.Apply(snapshot, revision, animateAccent);
        // Accent-only updates leave window materials, title bars and theme layout alone.
        if (animateAccent) return;
        Root.RequestedTheme = snapshot.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _windowBrush.Color = ThemeResources.ToColor(snapshot[ThemeToken.Window]);
        if (_window is null) return;
        var title = _window.AppWindow.TitleBar;
        title.ForegroundColor = ThemeResources.ToColor(snapshot[ThemeToken.Text]);
        title.BackgroundColor = ThemeResources.ToColor(snapshot[ThemeToken.Window]);
        title.ButtonForegroundColor = title.ForegroundColor;
        title.ButtonBackgroundColor = title.BackgroundColor;
        title.ButtonHoverBackgroundColor = ThemeResources.ToColor(snapshot[ThemeToken.ControlHover]);
        title.ButtonHoverForegroundColor = title.ForegroundColor;
        title.ButtonPressedBackgroundColor = ThemeResources.ToColor(snapshot[ThemeToken.ControlPressed]);
        title.ButtonPressedForegroundColor = title.ForegroundColor;
        title.InactiveForegroundColor = ThemeResources.ToColor(snapshot[ThemeToken.TextSecondary]);
        title.InactiveBackgroundColor = title.BackgroundColor;
        title.ButtonInactiveForegroundColor = title.InactiveForegroundColor;
        title.ButtonInactiveBackgroundColor = title.BackgroundColor;
        if (!_useBackdrop) return;
        if (snapshot.EffectiveMaterial == ThemeMaterial.Solid)
        {
            _window.SystemBackdrop = null;
        }
        else
        {
            // Compatibility material until P3 replaces/tunes the frosted renderer.
            if (_window.SystemBackdrop is not GaussianBlurBackdrop)
            {
                var backdrop = new GaussianBlurBackdrop();
                backdrop.Failed += () => Root.DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsDisposed || _window?.SystemBackdrop != backdrop) return;
                    _backdropFailed = true;
                    Apply(Resources.Snapshot, Resources.Revision);
                    ThemeService.RefreshOwners();
                    OutputLog.Write("WARN", "THEME", "Frosted renderer failed; an opaque scope background is active.");
                });
                _window.SystemBackdrop = backdrop;
            }
            if (!_backdropFailed)
            {
                var color = _windowBrush.Color;
                color.A = snapshot.IsDark ? (byte)225 : (byte)235;
                _windowBrush.Color = color;
            }
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (_window is not null)
        {
            _monitor?.Dispose();
            _monitor = null;
            _window.Closed -= OnClosed;
            if (_useBackdrop) _window.SystemBackdrop = null;
        }
        Root.Resources.MergedDictionaries.Remove(Resources.Dictionary);
        Root.RequestedTheme = _previousTheme;
        ThemeService.Detach(this);
        _window = null;
    }
}
