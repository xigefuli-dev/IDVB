using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using IDVBuff.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Helps;

/// <summary>
/// Full-page, modal onboarding guide.  Each step shades the application,
/// optionally outlines a target, and presents an explanation above the shade.
/// </summary>
public sealed class EmphasisGuide : IDisposable
{
    private const int DefaultNextDelaySeconds = 3;
    private readonly Panel _host;
    private readonly Grid _overlay = new();
    private readonly Canvas _filterLayer = new();
    private readonly Canvas _markerLayer = new() { IsHitTestVisible = false };
    private readonly Grid _informationLayer = new();
    private readonly Grid _previewLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Image _previewImage = new()
    {
        MaxWidth = 1400,
        MaxHeight = 900,
        Stretch = Stretch.Uniform
    };
    private readonly Border[] _filterSegments = new Border[4];
    private readonly Border _marker = new()
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0)),
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(4),
        Visibility = Visibility.Collapsed
    };
    private EmphasisGuideStep? _activeStep;
    private Border? _activeCard;
    private Rect? _lastTargetBounds;
    private bool _focusVisualsInitialized;
    private DispatcherTimer? _targetTrackingTimer;
    private CancellationTokenSource? _lifetime;
    private bool _isRunning;

    public EmphasisGuide(Panel host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        for (var index = 0; index < _filterSegments.Length; index++)
        {
            var segment = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 38, 38, 38))
            };
            _filterSegments[index] = segment;
            _filterLayer.Children.Add(segment);
        }
        _overlay.Children.Add(_filterLayer);
        _markerLayer.Children.Add(_marker);
        _overlay.Children.Add(_markerLayer);
        _overlay.Children.Add(_informationLayer);
        _previewLayer.Background = new SolidColorBrush(Color.FromArgb(235, 0, 0, 0));
        _previewLayer.Children.Add(_previewImage);
        _previewImage.PointerPressed += (_, e) => e.Handled = true;
        _previewLayer.PointerPressed += (_, _) => HidePreview();
        _overlay.Children.Add(_previewLayer);
    }

    /// <summary>Shows the supplied steps sequentially.  Calling it again while active is invalid.</summary>
    public async Task ShowAsync(IEnumerable<EmphasisGuideStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (_isRunning)
            throw new InvalidOperationException("同一个着重引导不能同时运行两次。");

        var guideSteps = steps.ToArray();
        if (guideSteps.Length == 0)
            return;

        _isRunning = true;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _host.Children.Add(_overlay);
        try
        {
            foreach (var step in guideSteps)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (step.EnterAsync is not null)
                    await step.EnterAsync(_lifetime.Token);
                await ShowStepAsync(step, _lifetime.Token);
            }
        }
        finally
        {
            _host.Children.Remove(_overlay);
            _informationLayer.Children.Clear();
            _marker.Visibility = Visibility.Collapsed;
            StopTrackingTarget();
            HidePreview();
            _lifetime.Dispose();
            _lifetime = null;
            _isRunning = false;
        }
    }

    private async Task ShowStepAsync(EmphasisGuideStep step, CancellationToken cancellationToken)
    {
        _informationLayer.Children.Clear();
        StartTrackingTarget(step);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 120,
            MinHeight = 36
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkMessage = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 181, 71)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var descriptionScroller = CreateScrollableDescription(
            CreateDescriptionBlock(step), checkMessage, step.ImageUris, step);
        var card = new Border
        {
            Width = 360,
            Height = Math.Clamp(_host.ActualHeight - 32, 180, 420),
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 31, 31, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 92, 92, 92)),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = GridLength.Auto }
                },
                Children =
                {
                    new TextBlock { Text = step.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    descriptionScroller,
                    button
                }
            }
        };
        Grid.SetRow(descriptionScroller, 1);
        Grid.SetRow(button, 2);
        _informationLayer.Children.Add(card);
        _activeCard = card;

        var seconds = step.NextButtonDelay?.TotalSeconds is double requested
            ? Math.Max(0, (int)Math.Ceiling(requested))
            : DefaultNextDelaySeconds;
        button.IsEnabled = seconds == 0;
        button.Content = AdvanceButtonText(step, seconds);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            seconds--;
            button.Content = AdvanceButtonText(step, seconds);
            if (seconds <= 0)
            {
                timer.Stop();
                button.IsEnabled = true;
            }
        };
        if (seconds > 0)
            timer.Start();

        button.Click += async (_, _) =>
        {
            if (step.CheckAsync is null)
            {
                completion.TrySetResult();
                return;
            }

            button.IsEnabled = false;
            button.Content = "正在检查…";
            try
            {
                var result = await step.CheckAsync(cancellationToken);
                if (result.IsPassed)
                {
                    completion.TrySetResult();
                    return;
                }

                checkMessage.Text = string.IsNullOrWhiteSpace(result.Message)
                    ? "尚未完成，请按提示重新操作后再次检查。"
                    : result.Message;
                checkMessage.Visibility = Visibility.Visible;
                button.Content = "检查";
                button.IsEnabled = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        };

        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            timer.Stop();
            _activeCard = null;
            StopTrackingTarget();
        }
    }

    private static string AdvanceButtonText(EmphasisGuideStep step, int seconds)
    {
        var action = step.AdvanceButtonText ?? (step.CheckAsync is null ? "下一步" : "检查");
        return seconds > 0 ? $"{action}（{seconds}秒）" : action;
    }

    private static TextBlock CreateDescriptionBlock(EmphasisGuideStep step)
    {
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var segments = step.DescriptionSegmentsFactory?.Invoke();
        if (segments is null)
        {
            description.Text = step.Description;
            return description;
        }

        foreach (var segment in segments)
        {
            var run = new Run { Text = segment.Text };
            if (segment.UseAccent)
                run.Foreground = FluentTheme.Brush("AccentFillColorDefaultBrush");
            description.Inlines.Add(run);
        }
        return description;
    }

    private ScrollViewer CreateScrollableDescription(
        TextBlock description,
        TextBlock checkMessage,
        IReadOnlyList<string>? imageUris,
        EmphasisGuideStep step)
    {
        var content = new StackPanel
        {
            Spacing = 14,
            Children = { description, checkMessage }
        };
        if (imageUris is { Count: > 0 })
        {
            content.Children.Add(CreateThumbnailStrip(imageUris));
            content.Children.Add(new TextBlock
            {
                Text = "点击图片放大查看。",
                FontSize = 12,
                Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
            });
        }
        if (!string.IsNullOrWhiteSpace(step.TutorialVideoUri))
            content.Children.Add(CreateTutorialVideoButton(step));
        if (!string.IsNullOrWhiteSpace(step.ActionButtonText) && step.ActionAsync is not null)
            content.Children.Add(CreateActionButton(step));
        return new ScrollViewer
        {
            Margin = new Thickness(0, 14, 0, 14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
    }

    private static Button CreateActionButton(EmphasisGuideStep step)
    {
        var button = new Button { Content = step.ActionButtonText, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 };
        button.Click += async (_, _) => await step.ActionAsync!(CancellationToken.None);
        return button;
    }

    private static Button CreateTutorialVideoButton(EmphasisGuideStep step)
    {
        var button = new Button
        {
            Content = "观看视频教程",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36
        };
        button.Click += async (_, _) =>
        {
            try
            {
                var launched = await Launcher.LaunchUriAsync(new Uri(step.TutorialVideoUri!));
                if (launched)
                    step.VideoOpened?.Invoke();
            }
            catch
            {
                // A failed shell launch deliberately does not count as watching.
            }
        };
        return button;
    }

    private UIElement CreateThumbnailStrip(IReadOnlyList<string> imageUris)
    {
        var images = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var imageUri in imageUris)
        {
            var thumbnail = new Image
            {
                Source = new BitmapImage(new Uri(imageUri)),
                Width = 104,
                Height = 72,
                Stretch = Stretch.UniformToFill
            };
            var button = new Button
            {
                Width = 108,
                Height = 76,
                Padding = new Thickness(0),
                Content = thumbnail,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 92, 92, 92))
            };
            button.Click += (_, _) => ShowPreview(imageUri);
            images.Children.Add(button);
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = images
        };
    }

    private void ShowPreview(string imageUri)
    {
        _previewImage.Source = new BitmapImage(new Uri(imageUri));
        _previewLayer.Visibility = Visibility.Visible;
    }

    private void HidePreview()
    {
        _previewLayer.Visibility = Visibility.Collapsed;
        _previewImage.Source = null;
    }

    private void StartTrackingTarget(EmphasisGuideStep step)
    {
        StopTrackingTarget();
        _activeStep = step;
        // The mask is visual-only. Intercepting pointer input in its four
        // segments makes normal cursor movement and unrelated controls feel
        // blocked, especially while a user is completing an operation step.
        _filterLayer.IsHitTestVisible = false;
        _lastTargetBounds = null;
        _focusVisualsInitialized = false;
        _targetTrackingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _targetTrackingTimer.Tick += (_, _) => UpdateFocusVisuals();
        _targetTrackingTimer.Start();
        UpdateFocusVisuals();
    }

    private void StopTrackingTarget()
    {
        _targetTrackingTimer?.Stop();
        _targetTrackingTimer = null;
        _activeStep = null;
        _lastTargetBounds = null;
        _focusVisualsInitialized = false;
    }

    private void UpdateFocusVisuals()
    {
        if (_activeStep is null)
            return;

        if (_activeCard is not null)
            _activeCard.Height = Math.Clamp(_host.ActualHeight - 32, 180, 420);

        var bounds = _activeStep.TargetBounds ?? GetTargetBounds(
            _activeStep.TargetProvider?.Invoke() ?? _activeStep.Target);
        if (_focusVisualsInitialized && bounds == _lastTargetBounds)
            return;

        _focusVisualsInitialized = true;
        _lastTargetBounds = bounds;
        var hostWidth = Math.Max(0, _host.ActualWidth);
        var hostHeight = Math.Max(0, _host.ActualHeight);
        if (bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
        {
            _marker.Visibility = Visibility.Collapsed;
            SetFilterSegment(0, 0, 0, hostWidth, hostHeight);
            for (var index = 1; index < _filterSegments.Length; index++)
                SetFilterSegment(index, 0, 0, 0, 0);
            return;
        }

        var left = Math.Clamp(bounds.Value.X, 0, hostWidth);
        var top = Math.Clamp(bounds.Value.Y, 0, hostHeight);
        var right = Math.Clamp(bounds.Value.Right, 0, hostWidth);
        var bottom = Math.Clamp(bounds.Value.Bottom, 0, hostHeight);
        _marker.Visibility = right > left && bottom > top ? Visibility.Visible : Visibility.Collapsed;
        _marker.Width = Math.Max(0, right - left);
        _marker.Height = Math.Max(0, bottom - top);
        Canvas.SetLeft(_marker, left);
        Canvas.SetTop(_marker, top);

        // Four shaded regions form a real hole around the entire target row.
        SetFilterSegment(0, 0, 0, hostWidth, top);
        SetFilterSegment(1, 0, top, left, bottom - top);
        SetFilterSegment(2, right, top, hostWidth - right, bottom - top);
        SetFilterSegment(3, 0, bottom, hostWidth, hostHeight - bottom);
    }

    private void SetFilterSegment(int index, double left, double top, double width, double height)
    {
        var segment = _filterSegments[index];
        segment.Visibility = width > 0 && height > 0 ? Visibility.Visible : Visibility.Collapsed;
        segment.Width = Math.Max(0, width);
        segment.Height = Math.Max(0, height);
        Canvas.SetLeft(segment, left);
        Canvas.SetTop(segment, top);
    }

    private Rect? GetTargetBounds(FrameworkElement? target)
    {
        if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            return null;
        return target.TransformToVisual(_host).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
    }
}

/// <summary>One developer-authored guide state.</summary>
public sealed record EmphasisGuideStep(
    string Title,
    string Description,
    FrameworkElement? Target = null,
    Rect? TargetBounds = null,
    TimeSpan? NextButtonDelay = null,
    Func<CancellationToken, Task>? EnterAsync = null,
    Func<CancellationToken, Task<EmphasisGuideCheckResult>>? CheckAsync = null,
    Func<IReadOnlyList<EmphasisGuideTextSegment>>? DescriptionSegmentsFactory = null,
    IReadOnlyList<string>? ImageUris = null,
    Func<FrameworkElement?>? TargetProvider = null,
    string? TutorialVideoUri = null,
        Action? VideoOpened = null,
        string? ActionButtonText = null,
        Func<CancellationToken, Task>? ActionAsync = null,
        string? AdvanceButtonText = null)
{
    /// <summary>Whether this step leaves the highlighted application surface operable.</summary>
    public bool RequiresUserOperation => CheckAsync is not null;
}

/// <summary>A guide-description segment; accented segments use the active theme blue.</summary>
public sealed record EmphasisGuideTextSegment(string Text, bool UseAccent = false);

/// <summary>The developer-provided decision returned after a user-operation check.</summary>
public sealed record EmphasisGuideCheckResult(bool IsPassed, string? Message = null)
{
    public static EmphasisGuideCheckResult Passed { get; } = new(true);

    public static EmphasisGuideCheckResult TryAgain(string? message = null) => new(false, message);
}
