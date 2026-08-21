using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using IDVBuff.Survey.Domain;
using XamlWindow = Microsoft.UI.Xaml.Window;
using IDVBuff.Core.Contracts;
using WinRT.Interop;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Small interactive match controller. This window is intentionally separate
/// from both the click-through map overlay and the full-screen manual selector.
/// </summary>
public sealed partial class MapControlPanelWindow : IDisposable
{
    private readonly Func<string, Task> _beginMatch;
    private readonly Func<string, Task> _beginSurveyMatch;
    private readonly Func<Task<IReadOnlyList<string>>> _getMapClasses;
    private readonly Func<string?> _getLastSelectedMapClass;
    private readonly Func<string, Task> _saveLastSelectedMapClass;
    private readonly Func<bool> _isAutomaticMapCacheEnabled;
    private readonly Func<bool, Task> _endMatch;
    private readonly Func<SurveyStatusSnapshot> _getSurveyStatus;
    private readonly Func<Task<MapMatchSnapshot>>? _activateSurveyMatch;
    private readonly Func<Task<MapVariantSelectionContext?>>? _getVariantContext;
    private readonly Func<Guid, Task>? _switchVariant;
    private readonly ICaptureProtectionService? _captureProtection;
    private readonly TextBlock _stateText = new()
    {
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 218, 229))
    };
    private readonly TextBlock _messageText = new()
    {
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 184, 77)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _beginButton = new()
    {
        Content = "开始对局",
        MinHeight = 40,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _endButton = new()
    {
        Content = "结束对局",
        MinHeight = 40,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ToggleSwitch _surveyModeToggle = new()
    {
        Header = "直接激活测绘模式",
        OffContent = "普通对局",
        OnContent = "测绘模式",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsOn = false
    };
    private readonly ComboBox _classComboBox = new()
    {
        Header = new TextBlock
        {
            Text = "地图模式（Class）",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        },
        MinHeight = 38,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = "请选择地图模式"
    };
    private readonly TextBlock _variantHeading = new()
    {
        Text = "可能存在的变体",
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 174, 184, 198)),
        Visibility = Visibility.Collapsed
    };
    private readonly StackPanel _variantButtons = new() { Spacing = 8 };
    private readonly ScrollViewer _variantScroller = new()
    {
        MaxHeight = 240,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Visibility = Visibility.Collapsed
    };
    private XamlWindow? _window;
    private string? _pendingClass;
    private MapVariantSelectionContext? _variantContext;
    private IReadOnlyList<string> _mapClasses = [];
    private MapMatchSnapshot _snapshot;
    private IntPtr _gameWindowHandle;
    private bool _isVisible;
    private bool _updatingSurveyToggle;
    private bool _suppressClassSelectionChanged;
    private bool _disposed;
    private Task _lastMapClassSaveTask = Task.CompletedTask;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;

    public MapControlPanelWindow(
        Func<string, Task> beginMatch,
        Func<Task<IReadOnlyList<string>>> getMapClasses,
        Func<string?> getLastSelectedMapClass,
        Func<string, Task> saveLastSelectedMapClass,
        Func<bool> isAutomaticMapCacheEnabled,
        Func<bool, Task> endMatch,
        Func<SurveyStatusSnapshot> getSurveyStatus,
        Func<string, Task>? beginSurveyMatch = null,
        Func<Task<MapMatchSnapshot>>? activateSurveyMatch = null,
        Func<Task<MapVariantSelectionContext?>>? getVariantContext = null,
        Func<Guid, Task>? switchVariant = null,
        ICaptureProtectionService? captureProtection = null)
    {
        _beginMatch = beginMatch;
        _beginSurveyMatch = beginSurveyMatch ?? beginMatch;
        _getMapClasses = getMapClasses;
        _getLastSelectedMapClass = getLastSelectedMapClass;
        _saveLastSelectedMapClass = saveLastSelectedMapClass;
        _isAutomaticMapCacheEnabled = isAutomaticMapCacheEnabled;
        _endMatch = endMatch;
        _getSurveyStatus = getSurveyStatus;
        _activateSurveyMatch = activateSurveyMatch;
        _getVariantContext = getVariantContext;
        _switchVariant = switchVariant;
        _captureProtection = captureProtection;
        _beginButton.Click += BeginButton_Click;
        _endButton.Click += EndButton_Click;
        _surveyModeToggle.Toggled += SurveyModeToggle_Toggled;
    }

    public bool IsVisible => _isVisible;

    public async Task ShowAsync(
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        MapMatchSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!gameBounds.IsValid || gameWindowHandle == IntPtr.Zero)
            throw new ArgumentException("Game window bounds are unavailable.");

        _gameWindowHandle = gameWindowHandle;
        _snapshot = snapshot;
        _mapClasses = (await _getMapClasses())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_mapClasses.Count == 0)
            throw new InvalidOperationException("地图库中还没有可用的地图模式。");
        var rememberedClass = _getLastSelectedMapClass();
        _pendingClass = MapRuntimeSettingsRules.ResolveMapClass(
            _mapClasses,
            snapshot.IsStarted ? snapshot.MapClass : rememberedClass);
        if (!snapshot.IsStarted
            && _pendingClass is not null
            && !string.Equals(
                rememberedClass,
                _pendingClass,
                StringComparison.Ordinal))
        {
            QueueMapClassSave(_pendingClass);
        }
        _variantContext = snapshot.IsStarted && snapshot.Mode == MapRunMode.Normal
            && _getVariantContext is not null
                ? await _getVariantContext()
                : null;
        EnsureWindow();
        Refresh(snapshot);

        var dpi = GetDpiForWindow(gameWindowHandle);
        var scale = Math.Max(1d, (dpi == 0 ? 96d : dpi) / 96d);
        var width = (int)Math.Round(400d * scale);
        var desiredHeight = _variantContext is null
            ? 360d
            : Math.Clamp(390d + _variantContext.Options.Count * 72d, 360d, 620d);
        var height = (int)Math.Round(desiredHeight * scale);
        var margin = (int)Math.Round(16d * scale);
        _window!.AppWindow.MoveAndResize(new RectInt32(
            (int)Math.Round(gameBounds.X + gameBounds.Width) - width - margin,
            (int)Math.Round(gameBounds.Y) + margin,
            width,
            height));
        _window.Activate();
        RegisterCaptureProtection();
        _isVisible = true;
    }

    public void Refresh(MapMatchSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.IsStarted)
        {
            _pendingClass = snapshot.MapClass;
        }
        else if (_pendingClass is null
            || !_mapClasses.Any(name => string.Equals(
                name,
                _pendingClass,
                StringComparison.OrdinalIgnoreCase)))
        {
            _pendingClass = _mapClasses.FirstOrDefault();
        }

        _stateText.Text = snapshot.IsStarted
            ? $"对局状态：已开始 · 模式 {_pendingClass}"
            : "对局状态：已结束";
        _suppressClassSelectionChanged = true;
        try
        {
            _classComboBox.ItemsSource = _mapClasses;
            _classComboBox.SelectedItem = _pendingClass;
        }
        finally
        {
            _suppressClassSelectionChanged = false;
        }
        _classComboBox.IsEnabled = !snapshot.IsStarted;
        if (snapshot.IsStarted)
            SetSurveyToggle(snapshot.Mode == MapRunMode.Survey);
        _surveyModeToggle.IsEnabled = CanChangeSurveyMode(snapshot);
        _beginButton.Visibility = snapshot.IsStarted
            ? Visibility.Collapsed
            : Visibility.Visible;
        _beginButton.IsEnabled = _pendingClass is not null;
        _beginButton.Content = _surveyModeToggle.IsOn ? "开始测绘" : "开始对局";
        _endButton.Visibility = snapshot.IsStarted
            ? Visibility.Visible
            : Visibility.Collapsed;
        _messageText.Text = snapshot.IsStarted
            ? _isAutomaticMapCacheEnabled()
                ? "结束时将询问是否保存本局收集的稳定地图缩放值。"
                : "结束后将清空本局地图和玩家状态。"
            : _surveyModeToggle.IsOn
                ? "将直接创建或恢复测绘项目。"
                : $"模式 {_pendingClass}，可以开始对局。";
        RefreshVariantOptions(snapshot);
        ApplySurveyState(snapshot);
    }

    public void Reset(MapMatchSnapshot snapshot)
    {
        _variantContext = null;
        SetSurveyToggle(false);
        Refresh(snapshot);
    }

    public void Hide(bool restoreGameFocus = true)
    {
        if (_window is not null)
            _window.AppWindow.Hide();
        _isVisible = false;
        if (restoreGameFocus && _gameWindowHandle != IntPtr.Zero)
            SetForegroundWindow(_gameWindowHandle);
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 15, 20, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 62, 72, 86)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = BuildContent()
        };
        _window = new XamlWindow
        {
            Content = root,
            ExtendsContentIntoTitleBar = true
        };
        _window.Closed += (_, _) =>
        {
            _captureProtectionRegistration?.Dispose();
            _captureProtectionRegistration = null;
            _window = null;
            _isVisible = false;
            if (_gameWindowHandle != IntPtr.Zero)
                SetForegroundWindow(_gameWindowHandle);
        };
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    private UIElement BuildContent()
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Identity Vision Bridge 对局控件",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(
                Color.FromArgb(255, 255, 255, 255))
        });
        content.Children.Add(_stateText);
        _classComboBox.SelectionChanged += ClassComboBox_SelectionChanged;
        content.Children.Add(_classComboBox);
        content.Children.Add(_surveyModeToggle);
        content.Children.Add(_variantHeading);
        _variantScroller.Content = _variantButtons;
        content.Children.Add(_variantScroller);
        content.Children.Add(_messageText);
        content.Children.Add(_beginButton);
        content.Children.Add(_endButton);
        return content;
    }

    private void RefreshVariantOptions(MapMatchSnapshot snapshot)
    {
        _variantButtons.Children.Clear();
        var visible = snapshot.IsStarted
            && snapshot.Mode == MapRunMode.Normal
            && _variantContext is { Options.Count: > 1 };
        _variantHeading.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _variantScroller.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || _variantContext is null)
            return;

        foreach (var option in _variantContext.Options.OrderBy(item => item.SequenceNumber))
        {
            var title = option.IsPending
                ? $"变体 {option.VariantNumber} · 待对齐"
                : $"变体 {option.VariantNumber}";
            var label = new StackPanel { Spacing = 2 };
            label.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Left
            });
            label.Children.Add(new TextBlock
            {
                Text = option.MapName,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left
            });
            var button = new Button
            {
                Tag = option.MapId,
                Content = label,
                MinHeight = 64,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(option.IsCurrent ? 3 : 1),
                BorderBrush = new SolidColorBrush(option.IsCurrent
                    ? Color.FromArgb(255, 46, 132, 225)
                    : Color.FromArgb(255, 72, 80, 92)),
                IsEnabled = !option.IsCurrent
            };
            ToolTipService.SetToolTip(button, option.MapName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                $"{title}，{option.MapName}");
            button.Click += VariantButton_Click;
            _variantButtons.Children.Add(button);
        }
    }

    private void RegisterCaptureProtection()
    {
        if (_captureProtection is null || _window is null || _captureProtectionRegistration is not null)
            return;
        try
        {
            _captureProtectionRegistration = _captureProtection.RegisterWindow(
                WindowNative.GetWindowHandle(_window),
                CaptureProtectionWindowCategory.DisplayLayer,
                "对局控件");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchControl] 捕获保护登记失败：{exception.Message}");
        }
    }

    private async void VariantButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchVariant is null || sender is not Button { Tag: Guid mapId })
            return;
        SetActionsEnabled(false);
        try
        {
            Hide();
            await _switchVariant(mapId);
        }
        catch (Exception exception)
        {
            _messageText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void ClassComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressClassSelectionChanged
            || _snapshot.IsStarted
            || _classComboBox.SelectedItem is not string mapClass)
            return;
        _pendingClass = mapClass;
        Refresh(_snapshot);
        QueueMapClassSave(mapClass);
    }

    private async void BeginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingClass is not { } mapClass)
            return;
        SetActionsEnabled(false);
        try
        {
            await _lastMapClassSaveTask;
            var startSurvey = _surveyModeToggle.IsOn;
            if (startSurvey)
            {
                // Survey activation captures immediately. The control panel is
                // currently foreground, so return focus to dwrg.exe first.
                Hide();
            }

            var begin = startSurvey
                ? _beginSurveyMatch
                : _beginMatch;
            await begin(mapClass);
            if (!startSurvey)
                Hide();
        }
        catch (Exception exception)
        {
            _messageText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private async void EndButton_Click(object sender, RoutedEventArgs e)
    {
        SetActionsEnabled(false);
        try
        {
            var saveAutomaticMapCache = _snapshot.Mode != MapRunMode.Survey
                && _isAutomaticMapCacheEnabled()
                && await ConfirmAutomaticMapCacheSaveAsync();
            await _endMatch(saveAutomaticMapCache);
            _variantContext = null;
            Hide();
        }
        catch (Exception exception)
        {
            _messageText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private async Task<bool> ConfirmAutomaticMapCacheSaveAsync()
    {
        var xamlRoot = (_window?.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null)
            return false;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "保存本局地图缓存？",
            Content = "将从本局收集的稳定缩放样本中生成地图缓存。"
                + "如果本局对齐结果可能有误，请选择不保存。",
            PrimaryButtonText = "保存并退出",
            CloseButtonText = "不保存并退出",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetActionsEnabled(bool enabled)
    {
        _beginButton.IsEnabled = enabled
            && _pendingClass is not null;
        _endButton.IsEnabled = enabled;
        _surveyModeToggle.IsEnabled = enabled && CanChangeSurveyMode(_snapshot);
    }

    private void QueueMapClassSave(string mapClass)
    {
        var previous = _lastMapClassSaveTask;
        _lastMapClassSaveTask = SaveMapClassAfterAsync(previous, mapClass);
    }

    private async Task SaveMapClassAfterAsync(Task previous, string mapClass)
    {
        try
        {
            await previous;
        }
        catch
        {
            // A failed earlier write must not prevent the latest selection
            // from being persisted.
        }

        try
        {
            await _saveLastSelectedMapClass(mapClass);
        }
        catch (Exception exception)
        {
            // The current in-memory selection remains usable for this match.
            _messageText.Text = $"地图模式记忆保存失败：{exception.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;
        _isVisible = false;
        _window?.Close();
        _window = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
/*
 * 文件职责：MapControlPanelWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
