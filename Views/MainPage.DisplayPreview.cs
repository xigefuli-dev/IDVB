using Microsoft.UI.Xaml;
using System.Numerics;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private void ConnectDisplayPreviewSource(MapStatusPage source)
    {
        _displayPreviewSource = source;
        source.DisplayPreviewVisibilityChanged += DisplayPreviewVisibilityChanged;
        source.DisplayPreviewChanged += DisplayPreviewChanged;
    }

    private void DisconnectDisplayPreviewSource()
    {
        SetDisplayPreviewVisibility(false, animate: false);
        if (_displayPreviewSource is null)
            return;
        _displayPreviewSource.DisplayPreviewVisibilityChanged -= DisplayPreviewVisibilityChanged;
        _displayPreviewSource.DisplayPreviewChanged -= DisplayPreviewChanged;
        _displayPreviewSource = null;
    }

    private void DisplayPreviewVisibilityChanged(bool visible) =>
        SetDisplayPreviewVisibility(visible, animate: true);

    private void DisplayPreviewChanged(OverlaySkeletonPreviewState state) =>
        _displaySkeletonPreview.Update(state);

    private void PrepareDisplayPreviewMotion()
    {
        DisplaySkeletonPreviewHost.Opacity = 0d;
        DisplaySkeletonPreviewHost.Translation = new Vector3(0f, 12f, 0f);
        DisplaySkeletonPreviewHost.OpacityTransition = new ScalarTransition
        {
            Duration = TimeSpan.FromMilliseconds(140)
        };
        DisplaySkeletonPreviewHost.TranslationTransition = new Vector3Transition
        {
            Duration = TimeSpan.FromMilliseconds(180)
        };
    }

    private void SetDisplayPreviewVisibility(bool visible, bool animate)
    {
        var revision = ++_displayPreviewVisibilityRevision;
        if (visible)
        {
            DisplaySkeletonPreviewHost.Visibility = Visibility.Visible;
            if (!animate)
            {
                DisplaySkeletonPreviewHost.Opacity = 1d;
                DisplaySkeletonPreviewHost.Translation = Vector3.Zero;
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (revision != _displayPreviewVisibilityRevision)
                    return;
                DisplaySkeletonPreviewHost.Opacity = 1d;
                DisplaySkeletonPreviewHost.Translation = Vector3.Zero;
            });
            return;
        }

        DisplaySkeletonPreviewHost.Opacity = 0d;
        DisplaySkeletonPreviewHost.Translation = new Vector3(0f, 12f, 0f);
        if (!animate)
        {
            DisplaySkeletonPreviewHost.Visibility = Visibility.Collapsed;
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(190);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (revision == _displayPreviewVisibilityRevision)
                DisplaySkeletonPreviewHost.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }
}
