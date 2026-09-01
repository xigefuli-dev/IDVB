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
        DisconnectDisplayPreviewSource();
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
            var view = App.IsSafeMode && IsSafeModeRestrictedModule(moduleId)
                ? CreateSafeModeRestrictedView(moduleId)
                : _catalog.GetRequired(moduleId).CreateView();
            ModuleContentHost.Content = view;
            ConfigureMainContentScrolling(view);
            if (view is HelpPage helpPage)
            {
                helpPage.ActivateGuideRequested += HelpPage_ActivateGuideRequested;
                helpPage.SubscribeMapsGuideRequested += HelpPage_SubscribeMapsGuideRequested;
            }
            if (view is MapStatusPage mapStatusPage)
                ConnectDisplayPreviewSource(mapStatusPage);
            // Keep the main content entrance motion consistent across all modules.
            animateMainContent = true;
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

    private async void HelpPage_ActivateGuideRequested(object? sender, EventArgs e)
    {
        try
        {
            if (App.IsSafeMode)
            {
                await ShowSafeModeTutorialAsync();
                return;
            }
            await ShowRecommendedConfigurationGuideAsync();
        }
        catch (Exception exception)
        {
            TryLogModuleFailure("help-guide", exception);
        }
    }

    private async void HelpPage_SubscribeMapsGuideRequested(object? sender, EventArgs e)
    {
        try
        {
            await ShowMapSubscriptionGuideAsync();
        }
        catch (Exception exception)
        {
            TryLogModuleFailure("map-subscription-guide", exception);
        }
    }

    private async Task ShowSafeModeTutorialAsync()
    {
        NavigateTo("map-list");
        await Task.Yield();
        if (ModuleContentHost.XamlRoot is not { } xamlRoot)
            return;

        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "安全模式新手教程",
            Content = "安全模式下，IDVB 不会加载自动识别、自动对齐、游戏内显示层或插件。请在地图列表中导入或选择地图；打开后，地图会以普通窗口展示。\n\n如需完整的新手教程，请先在“主设置 - 安全模式”中关闭安全模式，并以管理员权限重新启动 IDVB。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }

    private static bool IsSafeModeRestrictedModule(string moduleId) => moduleId is
        "map-status" or "plugins";

    private static FrameworkElement CreateSafeModeRestrictedView(string moduleId)
    {
        if (moduleId == "map-status")
            return new SafeModeMapStatusPage();

        return new StackPanel
        {
            MaxWidth = 720,
            Margin = new Thickness(42, 36, 42, 64),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "安全模式已开启",
                    FontSize = 30,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "此功能在安全模式下不可用。请先在主设置中关闭安全模式，然后重新启动 IDVB。",
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
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
