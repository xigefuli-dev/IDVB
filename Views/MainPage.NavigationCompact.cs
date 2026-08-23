using IDVBuff.Modules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private static readonly TimeSpan NavigationResizeDuration =
        TimeSpan.FromMilliseconds(180);

    private bool _navigationResizeAnimationActive;
    private DateTimeOffset _navigationResizeStartedAt;
    private double _navigationResizeStartWidth;
    private double _navigationResizeTargetWidth;
    private double _navigationChromeOpacity = 1d;
    private double _navigationResizeStartOpacity;
    private double _navigationResizeTargetOpacity;

    private void NavigationCompact_Click(object sender, RoutedEventArgs e)
    {
        _navigationCompactPreference = !_navigationIsCompact;
        _layoutMemory.NavigationCompact = _navigationCompactPreference;
        _layoutMemory.Save();
        SetNavigationCompact(_navigationCompactPreference);
    }

    private void SetEditorNavigationCompact(bool compact) =>
        SetNavigationCompact(compact || _navigationCompactPreference);

    private bool TryNavigateToNextCompactChild(NavigationEntry parent)
    {
        var targets = EnumerateModuleEntries(parent.Children).ToArray();
        if (targets.Length == 0)
            return false;

        var currentIndex = Array.IndexOf(targets, _selectedNavigationEntry);
        var target = targets[(currentIndex + 1) % targets.Length];
        NavigateTo(target.ModuleId!, target);
        return true;
    }

    private static IEnumerable<NavigationEntry> EnumerateModuleEntries(
        IEnumerable<NavigationEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.ModuleId is not null)
                yield return entry;

            foreach (var child in EnumerateModuleEntries(entry.Children))
                yield return child;
        }
    }

    private void AnimateNavigationColumnWidth(double targetWidth)
    {
        StopNavigationResizeAnimation();
        _navigationResizeStartWidth = NavigationColumn.ActualWidth;
        if (_navigationResizeStartWidth <= 0d)
            _navigationResizeStartWidth = NavigationColumn.Width.Value;

        _navigationResizeTargetWidth = targetWidth;
        _navigationResizeStartOpacity = _navigationChromeOpacity;
        _navigationResizeTargetOpacity = _navigationIsCompact ? 0d : 1d;
        if (Math.Abs(_navigationResizeStartWidth - targetWidth) < 0.5d)
        {
            NavigationColumn.Width = new GridLength(targetWidth);
            UpdateNavigationChromeOpacity(_navigationResizeTargetOpacity);
            FinalizeNavigationResizeLayout();
            return;
        }

        _navigationResizeStartedAt = DateTimeOffset.UtcNow;
        _navigationResizeAnimationActive = true;
        CompositionTarget.Rendering += NavigationResize_Rendering;
    }

    private void NavigationResize_Rendering(object? sender, object args)
    {
        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _navigationResizeStartedAt).TotalMilliseconds
                / NavigationResizeDuration.TotalMilliseconds,
            0d,
            1d);
        var easedProgress = 1d - Math.Pow(1d - progress, 3d);
        NavigationColumn.Width = new GridLength(
            _navigationResizeStartWidth
                + ((_navigationResizeTargetWidth - _navigationResizeStartWidth)
                    * easedProgress));
        UpdateNavigationChromeOpacity(
            _navigationResizeStartOpacity
                + ((_navigationResizeTargetOpacity - _navigationResizeStartOpacity)
                    * easedProgress));

        if (progress < 1d)
            return;

        StopNavigationResizeAnimation();
        NavigationColumn.Width = new GridLength(_navigationResizeTargetWidth);
        UpdateNavigationChromeOpacity(_navigationResizeTargetOpacity);
        FinalizeNavigationResizeLayout();
        QueueNavigationLayoutRefreshAfterCompactChange();
    }

    private void PrepareNavigationResizeAnimation(bool compact)
    {
        if (!compact)
        {
            foreach (var row in _navigationRowElements.Values.ToArray())
                ApplyNavigationRowLayout(row);
        }

        foreach (var (entry, children) in _navigationChildrenElements)
        {
            if (!compact && entry.Node.IsExpanded)
                children.Visibility = Visibility.Visible;

            children.Translation = Vector3.Zero;
        }

        UpdateNavigationChromeOpacity(_navigationChromeOpacity);
    }

    private void UpdateNavigationChromeOpacity(double opacity)
    {
        _navigationChromeOpacity = opacity;
        foreach (var row in _navigationRowElements.Values)
        {
            if (row is not Button { Content: Grid contentGrid })
                continue;

            foreach (var textBlock in contentGrid.Children.OfType<TextBlock>())
                textBlock.Opacity = opacity;
        }

        foreach (var children in _navigationChildrenElements.Values)
            children.Opacity = opacity;
    }

    private void FinalizeNavigationResizeLayout()
    {
        foreach (var row in _navigationRowElements.Values.ToArray())
            ApplyNavigationRowLayout(row);

        foreach (var (entry, children) in _navigationChildrenElements)
        {
            children.Visibility = !_navigationIsCompact && entry.Node.IsExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
            children.Opacity = _navigationIsCompact ? 0d : 1d;
            children.Translation = Vector3.Zero;
        }
    }

    private void StopNavigationResizeAnimation()
    {
        if (!_navigationResizeAnimationActive)
            return;

        CompositionTarget.Rendering -= NavigationResize_Rendering;
        _navigationResizeAnimationActive = false;
    }

    private void UpdateNavigationCompactButtonAccessibility()
    {
        var description = _navigationIsCompact ? "展开导航栏" : "收起导航栏";
        ToolTipService.SetToolTip(NavigationCompactButton, description);
        AutomationProperties.SetName(NavigationCompactButton, description);
    }
}
