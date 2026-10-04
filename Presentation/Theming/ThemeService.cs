using System.Runtime.CompilerServices;
using IDVBuff.Appearance;
using IDVBuff.Diagnostics;
using IDVBuff.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeService
{
    private static readonly ConditionalWeakTable<FrameworkElement, OwnerResources> Owners = new();
    private static readonly ConditionalWeakTable<FrameworkElement, ThemeScope> Scopes = new();
    private static readonly List<WeakReference<OwnerResources>> LiveOwners = [];
    private static readonly List<WeakReference<ThemeScope>> LiveScopes = [];
    private static readonly object Gate = new();
    private static readonly UISettings UiSettings = new();
    private static readonly AccessibilitySettings Accessibility = new();
    private static AppearancePreferences _preferences = new();
    private static RgbColor? _scanModeAccent;
    private static SystemAppearance _system = new(false, RgbColor.Parse("#245DD8"));
    private static DispatcherQueue? _dispatcher;
    private static ThemeResources? _applicationResources;
    private static long _revision;
    private static int _systemRefreshPending;
    private static readonly Dictionary<ThemeProfile, ThemeSnapshot> ResolvedSnapshots = [];

    public static AppearancePreferences Preferences => _preferences;
    internal static ThemeResources ApplicationResources =>
        _applicationResources ??= new(Resolve(ThemeProfile.Application));

    public static void SetScanModeAccent(Color color, bool animate = false)
    {
        if (_dispatcher?.HasThreadAccess != true)
            throw new InvalidOperationException("扫描模式强调色必须在主 UI 线程应用。");
        var accent = new RgbColor(color.R, color.G, color.B);
        if (_scanModeAccent == accent) return;
        _scanModeAccent = accent;
        if (_preferences.AccentFollowsScanMode)
            Refresh("ScanModeAccentChanged", log: false, animateAccent: animate);
    }

    public static void Initialize(AppearancePreferences preferences, DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        _system = ReadSystem();
        try { ThemeResolver.Resolve(preferences, _system); _preferences = preferences; }
        catch (ArgumentException exception)
        {
            _preferences = new();
            OutputLog.Write("WARN", "THEME", "Invalid appearance; using the complete default palette without overwriting preferences.", exception);
        }
        lock (Gate) ResolvedSnapshots.Clear();
        _applicationResources ??= new(Resolve(ThemeProfile.Application));
        _applicationResources.Apply(Resolve(ThemeProfile.Application), Interlocked.Increment(ref _revision));
        Application.Current.Resources.MergedDictionaries.Add(_applicationResources.Dictionary);
        UiSettings.ColorValuesChanged += (_, _) => QueueSystemRefresh();
        UiSettings.AdvancedEffectsEnabledChanged += (_, _) => QueueSystemRefresh();
        // HighContrastChanged requires a UWP CoreWindow and throws in this desktop host.
        // Window scopes listen to WM_THEMECHANGED/WM_SETTINGCHANGE instead.
    }

    internal static void QueueSystemRefresh()
    {
        if (Interlocked.Exchange(ref _systemRefreshPending, 1) != 0) return;
        if (_dispatcher?.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _systemRefreshPending, 0);
            try
            {
                var system = ReadSystem();
                if (system == _system) return;
                foreach (var profile in Enum.GetValues<ThemeProfile>()) ThemeResolver.Resolve(_preferences, system, profile);
                _system = system;
                Refresh("SystemAppearanceChanged");
            }
            catch (Exception exception)
            {
                OutputLog.Write("ERROR", "THEME", "System appearance refresh failed; retaining current resources.", exception);
            }
        }) != true) Interlocked.Exchange(ref _systemRefreshPending, 0);
    }

    public static void Apply(AppearancePreferences preferences)
    {
        if (_dispatcher?.HasThreadAccess != true)
            throw new InvalidOperationException("主题设置必须在主 UI 线程应用。");
        preferences.Validate();
        // Validate every workspace before changing any visible resource.
        foreach (var profile in Enum.GetValues<ThemeProfile>()) ThemeResolver.Resolve(preferences, _system, profile);
        var previous = _preferences;
        _preferences = preferences;
        try
        {
            Refresh("UserPreferenceChanged");
            MainProgramPreferences.SaveAppearance(preferences);
        }
        catch
        {
            _preferences = previous;
            Refresh("PreferenceRollback");
            throw;
        }
    }

    public static ThemeScope AttachWindow(Window window, FrameworkElement root,
        ThemeProfile profile = ThemeProfile.Application, bool useBackdrop = false)
    {
        var scope = AttachRegion(root, profile);
        scope.ConnectWindow(window, useBackdrop);
        return scope;
    }

    public static ThemeScope AttachRegion(FrameworkElement root, ThemeProfile profile)
    {
        if (Scopes.TryGetValue(root, out _)) throw new InvalidOperationException("该主题根已经注册。");
        var scope = new ThemeScope(root, profile);
        Scopes.Add(root, scope);
        lock (Gate) LiveScopes.Add(new(scope));
        scope.Apply(Resolve(profile), Interlocked.Increment(ref _revision));
        RefreshOwners();
        return scope;
    }

    internal static void Detach(ThemeScope scope)
    {
        Scopes.Remove(scope.Root);
        RefreshOwners();
    }

    public static ThemeResources For(FrameworkElement owner) => Owners.GetValue(owner, element =>
    {
        var resources = new OwnerResources(element, ResolveFor(element));
        lock (Gate)
        {
            LiveOwners.RemoveAll(reference => !reference.TryGetTarget(out _));
            LiveOwners.Add(new(resources));
        }
        return resources;
    }).Resources;

    private static ThemeSnapshot Resolve(ThemeProfile profile)
    {
        lock (Gate)
        {
            if (!ResolvedSnapshots.TryGetValue(profile, out var snapshot))
                ResolvedSnapshots[profile] = snapshot = ThemeResolver.Resolve(_preferences, _system,
                    profile, scanModeAccent: _scanModeAccent);
            return snapshot;
        }
    }

    private static ThemeSnapshot ResolveFor(FrameworkElement owner)
        => FindScope(owner)?.Resources.Snapshot ?? Resolve(ThemeProfile.Application);

    internal static ThemeProfile ProfileFor(FrameworkElement owner) => FindScope(owner)?.Profile ?? ThemeProfile.Application;

    private static ThemeScope? FindScope(FrameworkElement owner)
    {
        DependencyObject? current = owner;
        while (current is not null)
        {
            if (current is FrameworkElement element && Scopes.TryGetValue(element, out var scope))
                return scope;
            current = VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent;
        }
        // Popups and dialogs may be outside the content visual tree but still belong to a window.
        if (owner.XamlRoot?.Content is FrameworkElement root && Scopes.TryGetValue(root, out var windowScope))
            return windowScope;
        // Detached controls use the app preference, then resolve their real scope on Loaded.
        return null;
    }

    private static void Refresh(string reason, bool log = true, bool animateAccent = false)
    {
        lock (Gate) ResolvedSnapshots.Clear();
        var revision = Interlocked.Increment(ref _revision);
        _applicationResources?.Apply(Resolve(ThemeProfile.Application), revision, animateAccent);
        ThemeScope[] scopes;
        lock (Gate)
        {
            LiveScopes.RemoveAll(reference => !reference.TryGetTarget(out var scope) || scope.IsDisposed);
            scopes = LiveScopes.Select(reference => reference.TryGetTarget(out var scope) ? scope : null).OfType<ThemeScope>().ToArray();
        }
        foreach (var scope in scopes)
        {
            var snapshot = Resolve(scope.Profile);
            scope.Dispatch(() => scope.Apply(snapshot, revision, animateAccent));
        }
        RefreshOwners(animateAccent);
        if (log)
            OutputLog.Write("INFO", "THEME", $"revision={revision}; reason={reason}; mode={_preferences.Mode}; material={_preferences.Material}; windowsOrRegions={scopes.Length}");
    }

    internal static void RefreshOwners(bool animateAccent = false)
    {
        OwnerResources[] owners;
        lock (Gate)
        {
            LiveOwners.RemoveAll(reference => !reference.TryGetTarget(out _));
            owners = LiveOwners.Select(reference => reference.TryGetTarget(out var owner) ? owner : null).OfType<OwnerResources>().ToArray();
        }
        foreach (var owner in owners) owner.Refresh(animateAccent);
    }

    private static SystemAppearance ReadSystem()
    {
        RgbColor Read(UIColorType type)
        {
            var c = UiSettings.GetColorValue(type); return new(c.R, c.G, c.B);
        }
        ContrastColors? contrast = null;
        if (Accessibility.HighContrast)
        {
            RgbColor SystemColor(UIElementType type)
            {
                var c = UiSettings.UIElementColor(type); return new(c.R, c.G, c.B);
            }
            contrast = new(SystemColor(UIElementType.Window), SystemColor(UIElementType.WindowText),
                SystemColor(UIElementType.Highlight), SystemColor(UIElementType.HighlightText),
                SystemColor(UIElementType.GrayText), SystemColor(UIElementType.Hotlight));
        }
        return new(Read(UIColorType.Foreground).Luminance > .5, Read(UIColorType.Accent), UiSettings.AdvancedEffectsEnabled, contrast);
    }

    private sealed class OwnerResources
    {
        private readonly WeakReference<FrameworkElement> _owner;
        public ThemeResources Resources { get; }
        public OwnerResources(FrameworkElement owner, ThemeSnapshot snapshot)
        {
            _owner = new(owner);
            Resources = new(snapshot, includeDictionary: false);
            owner.Loaded += (_, _) => Refresh();
            owner.ActualThemeChanged += (_, _) => Refresh();
        }
        public void Refresh(bool animateAccent = false)
        {
            if (!_owner.TryGetTarget(out var owner)) return;
            void Update() => Resources.Apply(ResolveFor(owner), Volatile.Read(ref _revision), animateAccent);
            if (owner.DispatcherQueue.HasThreadAccess) Update();
            else owner.DispatcherQueue.TryEnqueue(Update);
        }
    }
}
