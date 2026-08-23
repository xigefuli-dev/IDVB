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

    public MainPage()
    {
        InitializeComponent();
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

    private void ApplyNavigationRowLayout(FrameworkElement row)
    {
        if (row is not Button button
            || button.Tag is not NavigationEntry entry
            || button.Content is not Grid contentGrid)
        {
            return;
        }

        var compact = _navigationIsCompact;
        contentGrid.Width = compact ? 40d : double.NaN;
        contentGrid.HorizontalAlignment = compact
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        contentGrid.Margin = compact
            ? new Thickness(0)
            : entry.Parent is null
                ? new Thickness(0)
                : new Thickness(38, 0, 0, 0);

        if (contentGrid.ColumnDefinitions.Count >= 3)
        {
            contentGrid.ColumnDefinitions[0].Width = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(60);
            contentGrid.ColumnDefinitions[1].Width = compact
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            contentGrid.ColumnDefinitions[2].Width = compact
                ? new GridLength(0)
                : new GridLength(34);
        }

        foreach (var icon in FindDescendants<SymbolIcon>(contentGrid))
        {
            icon.HorizontalAlignment = compact
                ? HorizontalAlignment.Center
                : entry.Parent is null
                    ? HorizontalAlignment.Center
                    : HorizontalAlignment.Left;
        }

        foreach (var icon in FindDescendants<FontIcon>(contentGrid))
        {
            icon.HorizontalAlignment = compact
                ? HorizontalAlignment.Center
                : entry.Parent is null
                    ? HorizontalAlignment.Center
                    : HorizontalAlignment.Left;
        }

        // Only hide the navigation row's own labels. Recursing into the icon
        // control templates also finds their glyph TextBlocks and erases them.
        foreach (var textBlock in contentGrid.Children.OfType<TextBlock>())
        {
            textBlock.Visibility = compact
                ? Visibility.Collapsed
                : textBlock.Tag is NavigationEntry taggedEntry
                    ? taggedEntry.ExpansionVisibility
                    : Visibility.Visible;
        }
    }

    private NavigationEntry? GetVisibleNavigationEntry(NavigationEntry? entry)
    {
        while (entry is not null
            && (!IsNavigationEntryVisible(entry)
                || (_navigationIsCompact && entry.Parent is not null)))
        {
            entry = entry.Parent;
        }
        return entry;
    }

    private static bool IsNavigationEntryVisible(NavigationEntry entry)
    {
        for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
        {
            if (!parent.Node.IsExpanded)
                return false;
        }

        return true;
    }

    private void TryPositionInitialNavigationIndicator(NavigationEntry entry)
    {
        if (_selectionIndicatorPositioned)
            return;

        if (TryAnimateNavigationIndicator(
                NavigationSelectionIndicator,
                entry,
                duration: null))
        {
            _selectionIndicatorPositioned = true;
        }
    }

    private void QueueSelectionIndicatorAnimation(NavigationEntry entry)
    {
        _pendingSelectionTarget = entry;
        if (TryAnimateNavigationIndicator(
                NavigationSelectionIndicator,
                entry,
                NavigationSelectionDuration))
        {
            _pendingSelectionTarget = null;
            return;
        }

        if (_selectionUpdateQueued)
            return;

        _selectionUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _selectionUpdateQueued = false;
            if (_pendingSelectionTarget is not { } pending)
                return;

            _pendingSelectionTarget = null;
            QueueSelectionIndicatorAnimation(pending);
        });
    }

    private bool TryAnimateNavigationIndicator(
        Border indicator,
        NavigationEntry target,
        TimeSpan? duration)
    {
        if (TryGetNavigationRow(target) is not FrameworkElement container
            || container.Visibility != Visibility.Visible)
        {
            return false;
        }

        var targetPoint = container.TransformToVisual(NavigationSurface)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetTranslationY = (float)(targetPoint.Y - indicator.Margin.Top);
        indicator.TranslationTransition = duration is { } transitionDuration
            ? new Vector3Transition { Duration = transitionDuration }
            : null;
        indicator.Translation = new Vector3(
            indicator.Translation.X,
            targetTranslationY,
            indicator.Translation.Z);
        return true;
    }

    private FrameworkElement? TryGetNavigationRow(NavigationEntry entry)
        => _navigationRowElements.TryGetValue(entry, out var row) ? row : null;

    private void HideNavigationHoverIndicator()
    {
        _hoveredNavigationEntry = null;
        _navigationHoverIndicatorShown = false;
        AnimateNavigationIndicatorOpacity(0f, NavigationHoverExitDuration);
    }

    private void AnimateNavigationIndicatorOpacity(float targetOpacity, TimeSpan duration)
    {
        NavigationHoverIndicator.OpacityTransition = new ScalarTransition
        {
            Duration = duration
        };
        NavigationHoverIndicator.Opacity = targetOpacity;
    }

    private void NavigateTo(string moduleId, NavigationEntry? navigationEntry = null)
    {
        SetNavigationCompact(_navigationCompactPreference);

        if (navigationEntry is not null)
        {
            _selectedNavigationEntry = navigationEntry;
            var visibleSelectionTarget = GetVisibleNavigationEntry(navigationEntry)
                ?? navigationEntry;
            if (_selectionIndicatorPositioned)
                QueueSelectionIndicatorAnimation(visibleSelectionTarget);
            else
                QueueInitialNavigationIndicatorPosition(visibleSelectionTarget);
        }

        var animateMainContent = true;
        try
        {
            var view = _catalog.GetRequired(moduleId).CreateView();
            ModuleContentHost.Content = view;
            ConfigureMainContentScrolling(view);
            // TeachingTip performs native popup placement from its target's visual.
            // Keep pages that host targeted tips out of the translated composition
            // chain; MapListPage already uses this stable path for its import tip.
            animateMainContent = view is not MapListPage and not SettingsPage;
            if (view is MapListPage mapListPage)
            {
                mapListPage.ParentScrollViewer = MainContentHost;
                mapListPage.NavigationCompactStateChanged = SetEditorNavigationCompact;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Module '{moduleId}' failed to create its view: {exception}");
            TryLogModuleFailure(moduleId, exception);
            ModuleContentHost.Content = CreateModuleFailureView(moduleId, exception);
        }
        try
        {
            if (animateMainContent)
                PlayMainContentEnterAnimation();
            else
                ResetMainContentVisual();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Module '{moduleId}' enter animation failed: {exception}");
            TryLogModuleFailure(moduleId, exception, "enter-animation");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateTo("settings");

    private void PlayMainContentEnterAnimation()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(MainContentHost, true);
        var visual = ElementCompositionPreview.GetElementVisual(MainContentHost);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.StopAnimation("Scale");
        visual.Opacity = 0;
        visual.Scale = Vector3.One;
        MainContentHost.Translation = new Vector3(0f, 14f, 0f);

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, CreateMainEase(visual));
        opacity.Duration = MainContentEnterDuration;

        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(0f, 14f, 0f));
        translation.InsertKeyFrame(1f, Vector3.Zero, CreateMainEase(visual));
        translation.Duration = MainContentEnterDuration;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private void ResetMainContentVisual()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(MainContentHost, true);
        var visual = ElementCompositionPreview.GetElementVisual(MainContentHost);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.StopAnimation("Scale");
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;
        MainContentHost.Translation = Vector3.Zero;
    }

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateMainEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    private static void PlayDetailTriggerFeedback(UIElement trigger)
    {
        var visual = ElementCompositionPreview.GetElementVisual(trigger);
        if (trigger is FrameworkElement element)
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);

        visual.Scale = new Vector3(0.985f, 0.985f, 1);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, Vector3.One, CreateDetailEase(visual));
        animation.Duration = DetailFeedbackDuration;
        visual.StartAnimation("Scale", animation);
    }

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateDetailEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
}
