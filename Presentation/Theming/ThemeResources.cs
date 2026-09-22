using IDVBuff.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Presentation.Theming;

internal sealed class ThemeResources
{
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

    public void Apply(ThemeSnapshot snapshot, long revision)
    {
        if (revision < Revision) return;
        Snapshot = snapshot;
        Revision = revision;
        foreach (var (token, brush) in _brushes) brush.Color = ToColor(snapshot[token]);
        Changed?.Invoke(snapshot);
    }

    public static Windows.UI.Color ToColor(RgbColor color) =>
        Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
}
