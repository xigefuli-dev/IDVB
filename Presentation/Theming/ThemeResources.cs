using IDVBuff.Appearance;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Presentation.Theming;

internal sealed class ThemeResources
{
    [ThreadStatic] private static List<ThemeResources>? _animations;
    private readonly List<(SolidColorBrush Brush, Windows.UI.Color From, Windows.UI.Color To)> _transition = [];
    private long _transitionStarted;
    private readonly Dictionary<ThemeToken, SolidColorBrush> _brushes = [];
    public ResourceDictionary Dictionary { get; } = new();
    public ThemeSnapshot Snapshot { get; private set; }
    public long Revision { get; private set; }
    public event Action<ThemeSnapshot>? Changed;

    public ThemeResources(ThemeSnapshot snapshot, bool includeDictionary = true)
    {
        Snapshot = snapshot;
        foreach (var token in Enum.GetValues<ThemeToken>())
            _brushes[token] = new SolidColorBrush(ToColor(snapshot[token]));
        if (!includeDictionary) return;
        // Each dictionary belongs to one scope. Its brush identities stay stable even
        // when only the accent changes, which does not trigger a WinUI theme change.
        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        {
            var resources = new ResourceDictionary();
            foreach (var (token, brush) in _brushes) resources[$"Idvb{token}Brush"] = brush;
            foreach (var (key, token) in ThemeResourceKeys.Brushes) resources[key] = _brushes[token];
            Dictionary.ThemeDictionaries[theme] = resources;
        }
    }

    public SolidColorBrush this[ThemeToken token] => _brushes[token];

    public void Apply(ThemeSnapshot snapshot, long revision, bool animateAccent = false)
    {
        if (revision < Revision) return;
        if (ReferenceEquals(Snapshot, snapshot) && revision == Revision) return;
        // Stable brush identities keep native controls connected. Resolve the palette
        // and notify observers once; animation frames only touch changed brush colors.
        _transition.Clear();
        var animate = animateAccent && !snapshot.IsHighContrast;
        Snapshot = snapshot;
        Revision = revision;
        foreach (var (token, brush) in _brushes)
        {
            var target = ToColor(snapshot[token]);
            // Include invariant foregrounds so every frame (and the final endpoint)
            // starts from the intended color before contrast correction.
            if (brush.Color.Equals(target) && !(animate && token is
                ThemeToken.OnAccent or ThemeToken.SelectionText or ThemeToken.TextSelectionText)) continue;
            if (animate) _transition.Add((brush, brush.Color, target));
            else brush.Color = target;
        }
        if (_transition.Count > 0)
        {
            _transitionStarted = Stopwatch.GetTimestamp();
            _animations ??= [];
            if (!_animations.Contains(this))
            {
                if (_animations.Count == 0) CompositionTarget.Rendering += RenderTransitions;
                _animations.Add(this);
            }
        }
        Changed?.Invoke(snapshot);
    }

    private static void RenderTransitions(object? sender, object args)
    {
        if (_animations is not { } animations) return;
        var now = Stopwatch.GetTimestamp();
        for (var i = animations.Count - 1; i >= 0; i--)
        {
            var resources = animations[i];
            var progress = Math.Clamp(Stopwatch.GetElapsedTime(resources._transitionStarted, now).TotalMilliseconds / 460, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            foreach (var (brush, from, to) in resources._transition)
            {
                static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
                var color = Windows.UI.Color.FromArgb(255, Mix(from.R, to.R, eased),
                    Mix(from.G, to.G, eased), Mix(from.B, to.B, eased));
                if (!brush.Color.Equals(color)) brush.Color = color;
            }
            if (resources._transition.Count > 0)
            {
                resources.ProtectText(ThemeToken.OnAccent, ThemeToken.Accent);
                resources.ProtectText(ThemeToken.OnAccent, ThemeToken.AccentHover);
                resources.ProtectText(ThemeToken.OnAccent, ThemeToken.AccentPressed);
                resources.ProtectText(ThemeToken.SelectionText, ThemeToken.Selection);
                resources.ProtectText(ThemeToken.SelectionText, ThemeToken.SelectionHover);
                resources.ProtectText(ThemeToken.TextSelectionText, ThemeToken.TextSelection);
            }
            if (progress >= 1 || resources._transition.Count == 0)
            {
                resources._transition.Clear();
                animations.RemoveAt(i);
            }
        }
        if (animations.Count == 0) CompositionTarget.Rendering -= RenderTransitions;
    }

    private void ProtectText(ThemeToken foreground, ThemeToken background)
    {
        static RgbColor Rgb(Windows.UI.Color color) => new(color.R, color.G, color.B);
        var brush = _brushes[foreground];
        var current = Rgb(brush.Color);
        var corrected = ThemeColorTransition.KeepReadable(current, Rgb(_brushes[background].Color),
            foreground == ThemeToken.SelectionText ? Snapshot.IsDark : current.Luminance > .5);
        if (corrected != current) brush.Color = ToColor(corrected);
    }

    public static Windows.UI.Color ToColor(RgbColor color) =>
        Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
}
