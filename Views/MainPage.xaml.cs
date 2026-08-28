using IDVBuff.Features.Maps;
using IDVBuff.Modules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Numerics;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MainPage : Page
{
    private static readonly TimeSpan NavigationSelectionDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan NavigationHoverEnterDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan NavigationHoverExitDuration = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan NavigationExpansionRotationDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan NavigationExpansionDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan MainContentEnterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DetailFeedbackDuration = TimeSpan.FromMilliseconds(100);
    private const double CompactNavigationWidth = 68d;

    private readonly ModuleCatalog _catalog = ModuleRegistration.CreateCatalog();
    private readonly IReadOnlyList<NavigationNode> _navigationNodes = ModuleRegistration.CreateNavigation();
    private NavigationEntry? _selectedNavigationEntry;
    private NavigationEntry? _pendingSelectionTarget;
    private bool _selectionUpdateQueued;
    private bool _selectionIndicatorPositioned;
    private bool _navigationLayoutRefreshPending;
    private NavigationEntry? _hoveredNavigationEntry;
    private bool _navigationHoverIndicatorShown;
    private bool _navigationIsCompact;
    private bool _navigationCompactPreference;
    private readonly ShellLayoutMemory _layoutMemory = ShellLayoutMemory.Load();
    private bool _hasSavedNavigationWidth;
    private GridLength _savedNavigationWidth;
    private readonly Dictionary<NavigationEntry, FrameworkElement> _navigationRowElements = [];
    private readonly Dictionary<NavigationEntry, FrameworkElement> _navigationExpansionGlyphElements = [];
    private readonly Dictionary<NavigationEntry, ItemsControl> _navigationChildrenElements = [];
    private readonly OverlaySkeletonPreview _displaySkeletonPreview = new();
    private MapStatusPage? _displayPreviewSource;
    private int _displayPreviewVisibilityRevision;

    public MainPage()
    {
        InitializeComponent();
        DisplaySkeletonPreviewHost.Children.Add(_displaySkeletonPreview);
        PrepareDisplayPreviewMotion();
        FluentTheme.RegisterThemeRoot(this);
        RootSurface.Background = FluentTheme.WindowBrush();
        foreach (var entry in NavigationEntry.CreateRoots(_navigationNodes))
            NavigationItems.Add(entry);
        HelpNavigationItem = CreateFooterNavigationEntry("帮助", Symbol.Help, "help");
        MainSettingsNavigationItem = CreateFooterNavigationEntry("主设置", Symbol.Setting, "main-settings");
        _navigationCompactPreference = _layoutMemory.NavigationCompact;
        ApplyInitialNavigationCompactPreference();
        Loaded += MainPage_Loaded;
    }

    public ObservableCollection<NavigationEntry> NavigationItems { get; } = [];
    public NavigationEntry HelpNavigationItem { get; }
    public NavigationEntry MainSettingsNavigationItem { get; }

    private static NavigationEntry CreateFooterNavigationEntry(string name, Symbol icon, string moduleId) =>
        new(new NavigationNode(name, icon, moduleId), parent: null);

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        NavigateTo("home", NavigationItems.First(entry => entry.ModuleId == "home"));
    }

    private void Navigation_ParentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is not NavigationEntry entry)
        {
            return;
        }

        PlayDetailTriggerFeedback(button);

        if (entry.ModuleId is { } moduleId)
        {
            NavigateTo(moduleId, entry);
        }
        else if (entry.Node.Children.Count > 0)
        {
            if (_navigationIsCompact && TryNavigateToNextCompactChild(entry))
                return;

            // The pointer remains over the parent after the click, so no
            // PointerExited event is raised.  Keeping its hover indicator while
            // the selected child becomes visible makes the two 40px indicators
            // overlap and look like one selection stopped between rows.
            HideNavigationHoverIndicator();
            entry.Node.ToggleExpanded();
            AnimateNavigationExpansion(entry);
        }
    }

    private void Navigation_ChildClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is not NavigationEntry entry
            || entry.ModuleId is not { } moduleId)
        {
            return;
        }

        PlayDetailTriggerFeedback(button);
        NavigateTo(moduleId, entry);
    }

    private void NavigationRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement row
            && row.Tag is NavigationEntry entry)
        {
            _navigationRowElements[entry] = row;
            ApplyNavigationRowLayout(row);
        }
    }

    private void NavigationRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement row
            && row.Tag is NavigationEntry entry
            && _navigationRowElements.TryGetValue(entry, out var current)
            && ReferenceEquals(current, row))
        {
            _navigationRowElements.Remove(entry);
        }
    }

    private void NavigationExpansionGlyph_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement glyph
            || glyph.Tag is not NavigationEntry entry)
        {
            return;
        }

        _navigationExpansionGlyphElements[entry] = glyph;
        glyph.Visibility = _navigationIsCompact
            ? Visibility.Collapsed
            : entry.ExpansionVisibility;
        glyph.CenterPoint = new Vector3(
            (float)glyph.ActualWidth / 2f,
            (float)glyph.ActualHeight / 2f,
            0f);
        glyph.RotationTransition = null;
        glyph.Rotation = entry.Node.IsExpanded ? 90f : 0f;
        glyph.RotationTransition = new ScalarTransition
        {
            Duration = NavigationExpansionRotationDuration
        };
    }

    private void NavigationExpansionGlyph_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement glyph
            && glyph.Tag is NavigationEntry entry
            && _navigationExpansionGlyphElements.TryGetValue(entry, out var current)
            && ReferenceEquals(current, glyph))
        {
            _navigationExpansionGlyphElements.Remove(entry);
        }
    }

    private void NavigationChildren_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl children
            || children.Tag is not NavigationEntry entry)
        {
            return;
        }

        _navigationChildrenElements[entry] = children;
        children.Visibility = _navigationIsCompact
            ? Visibility.Collapsed
            : entry.Node.IsExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
        children.OpacityTransition = null;
        children.TranslationTransition = null;
        children.Opacity = entry.Node.IsExpanded ? 1d : 0d;
        children.Translation = Vector3.Zero;
        children.OpacityTransition = new ScalarTransition
        {
            Duration = NavigationExpansionDuration
        };
    }

    private void NavigationChildren_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl children
            && children.Tag is NavigationEntry entry
            && _navigationChildrenElements.TryGetValue(entry, out var current)
            && ReferenceEquals(current, children))
        {
            _navigationChildrenElements.Remove(entry);
        }
    }

    private void AnimateNavigationExpansion(NavigationEntry entry)
    {
        AnimateNavigationExpansionGlyph(entry);
        AnimateNavigationChildren(entry);
    }

    private void AnimateNavigationExpansionGlyph(NavigationEntry entry)
    {
        if (!_navigationExpansionGlyphElements.TryGetValue(entry, out var glyph))
            return;

        glyph.CenterPoint = new Vector3(
            (float)glyph.ActualWidth / 2f,
            (float)glyph.ActualHeight / 2f,
            0f);
        glyph.Rotation = entry.Node.IsExpanded ? 90f : 0f;
    }

    private void AnimateNavigationChildren(NavigationEntry entry)
    {
        if (!_navigationChildrenElements.TryGetValue(entry, out var children))
        {
            RequestNavigationLayoutRefresh();
            return;
        }

        if (_navigationIsCompact)
        {
            children.Visibility = Visibility.Collapsed;
            children.Opacity = 0d;
            children.Translation = Vector3.Zero;
            return;
        }

        if (entry.Node.IsExpanded)
        {
            children.Visibility = Visibility.Visible;
            RequestNavigationLayoutRefresh();
        }

        children.Opacity = entry.Node.IsExpanded ? 1d : 0d;
        // Keep child rows in their final coordinate space throughout expansion.
        // TransformToVisual is also used to position the selection indicator;
        // animating this translation made it capture the transient -8px offset.
        children.Translation = Vector3.Zero;

        if (!entry.Node.IsExpanded)
        {
            var collapseTimer = DispatcherQueue.CreateTimer();
            collapseTimer.Interval = NavigationExpansionDuration;
            collapseTimer.IsRepeating = false;
            collapseTimer.Tick += (_, _) =>
            {
                collapseTimer.Stop();
                if (!entry.Node.IsExpanded)
                {
                    children.Visibility = Visibility.Collapsed;
                    RequestNavigationLayoutRefresh();
                }
            };
            collapseTimer.Start();
        }
    }

    private void Navigation_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var entry = TryGetNavigationEntry(e.OriginalSource as DependencyObject);
        if (entry is null)
        {
            HideNavigationHoverIndicator();
            return;
        }

        if (ReferenceEquals(entry, _hoveredNavigationEntry))
            return;

        _hoveredNavigationEntry = entry;
        var shouldFadeInAtTarget = !_navigationHoverIndicatorShown;
        if (TryAnimateNavigationIndicator(
                NavigationHoverIndicator,
                entry,
                shouldFadeInAtTarget ? null : NavigationHoverEnterDuration))
        {
            AnimateNavigationIndicatorOpacity(1f, NavigationHoverEnterDuration);
            _navigationHoverIndicatorShown = true;
        }
    }

    private void Navigation_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited is raised by the individual list/footer hosts as the
        // pointer moves between them. Keep the hover indicator alive while it
        // remains anywhere inside the navigation surface, so row-to-row motion
        // still uses its normal translation animation.
        var position = e.GetCurrentPoint(NavigationSurface).Position;
        if (position.X >= 0d
            && position.X <= NavigationSurface.ActualWidth
            && position.Y >= 0d
            && position.Y <= NavigationSurface.ActualHeight)
        {
            return;
        }

        HideNavigationHoverIndicator();
    }

    private NavigationEntry? TryGetNavigationEntry(DependencyObject? source)
    {
        while (source is not null && source != PrimaryNavigation)
        {
            if (source is FrameworkElement element
                && element.Tag is NavigationEntry taggedEntry)
            {
                return taggedEntry;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void QueueInitialNavigationIndicatorPosition(NavigationEntry? entry)
    {
        if (entry is null)
            return;

        DispatcherQueue.TryEnqueue(() =>
            TryPositionInitialNavigationIndicator(entry));
    }

    private void NavigationSurface_LayoutUpdated(object sender, object e)
    {
        if (!_selectionIndicatorPositioned
            && _selectedNavigationEntry is { } entry)
        {
            TryPositionInitialNavigationIndicator(entry);
        }

        if (!_navigationLayoutRefreshPending)
            return;

        _navigationLayoutRefreshPending = false;
        var selectionTarget = GetVisibleNavigationEntry(_selectedNavigationEntry);
        if (selectionTarget is not null)
            QueueSelectionIndicatorAnimation(selectionTarget);

        if (_hoveredNavigationEntry is { } hovered
            && IsNavigationEntryVisible(hovered))
        {
            TryAnimateNavigationIndicator(
                NavigationHoverIndicator,
                hovered,
                NavigationHoverEnterDuration);
        }
        else if (_hoveredNavigationEntry is not null)
        {
            HideNavigationHoverIndicator();
        }
    }

    private void RequestNavigationLayoutRefresh() =>
        _navigationLayoutRefreshPending = true;

    internal void SetNavigationCompact(bool compact)
    {
        if (_navigationIsCompact == compact)
            return;

        HideNavigationHoverIndicator();
        // Ignore intermediate LayoutUpdated notifications while rows are being
        // hidden/shown. Child rows do not have their final arranged position yet.
        _navigationLayoutRefreshPending = false;

        if (compact)
        {
            if (!_hasSavedNavigationWidth)
            {
                _savedNavigationWidth = NavigationColumn.Width;
                _hasSavedNavigationWidth = true;
            }
            _navigationIsCompact = true;
            PrepareNavigationResizeAnimation(compact);
            AnimateNavigationColumnWidth(CompactNavigationWidth);
        }
        else
        {
            _navigationIsCompact = false;
            var expandedWidth = _hasSavedNavigationWidth
                ? _savedNavigationWidth
                : new GridLength(250d);
            PrepareNavigationResizeAnimation(compact);
            AnimateNavigationColumnWidth(expandedWidth.Value);
        }

        UpdateNavigationCompactButtonAccessibility();
        QueueNavigationLayoutRefreshAfterCompactChange();
    }

    private void ApplyInitialNavigationCompactPreference()
    {
        if (!_navigationCompactPreference)
            return;

        // Apply the remembered state before the page is first rendered.  Calling
        // SetNavigationCompact from Loaded would briefly expose the expanded bar.
        _navigationIsCompact = true;
        _savedNavigationWidth = NavigationColumn.Width;
        _hasSavedNavigationWidth = true;
        NavigationColumn.Width = new GridLength(CompactNavigationWidth);
        UpdateNavigationChromeOpacity(0d);
        UpdateNavigationCompactButtonAccessibility();
    }

    private void QueueNavigationLayoutRefreshAfterCompactChange()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Compact mode changes both the navigation width and child visibility.
            // Force that layout to settle before reading TransformToVisual; otherwise
            // a selected child can retain the compact parent row's/stale Y position.
            NavigationSurface.UpdateLayout();

            var selectionTarget = GetVisibleNavigationEntry(_selectedNavigationEntry);
            if (selectionTarget is not null)
                QueueSelectionIndicatorAnimation(selectionTarget);

            RequestNavigationLayoutRefresh();
        });
    }
}
