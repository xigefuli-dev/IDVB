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
    private readonly Func<bool> _isSurveyModeAllowed;
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
    private IReadOnlyList<MapClassDiagnostic> _mapClassDiagnostics = [];
    private IReadOnlyList<MapClassDiagnostic> _renderedMapClassDiagnostics = [];
    private MapMatchSnapshot _snapshot;

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
        Func<bool> isSurveyModeAllowed,
        Func<string, Task>? beginSurveyMatch = null,
        Func<Task<MapMatchSnapshot>>? activateSurveyMatch = null,
        Func<Task<MapVariantSelectionContext?>>? getVariantContext = null,
        Func<Guid, Task>? switchVariant = null,
        ICaptureProtectionService? captureProtection = null,
        Func<Task>? correctMap = null)
    {
        _beginMatch = beginMatch;
        _beginSurveyMatch = beginSurveyMatch ?? beginMatch;
        _getMapClasses = getMapClasses;
        _getLastSelectedMapClass = getLastSelectedMapClass;
        _saveLastSelectedMapClass = saveLastSelectedMapClass;
        _isAutomaticMapCacheEnabled = isAutomaticMapCacheEnabled;
        _endMatch = endMatch;
        _getSurveyStatus = getSurveyStatus;
        _isSurveyModeAllowed = isSurveyModeAllowed;
        _activateSurveyMatch = activateSurveyMatch;
        _getVariantContext = getVariantContext;
        _switchVariant = switchVariant;
        _correctMap = correctMap;
        _captureProtection = captureProtection;
        MapClassDiagnosticCoordinator.Instance.SnapshotChanged += OnDiagnosticSnapshotChanged;
        _beginButton.Click += BeginButton_Click;
        _endButton.Click += EndButton_Click;
        _correctMapButton.Click += CorrectMapButton_Click;
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


        _snapshot = snapshot;
        _mapClasses = (await _getMapClasses())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var completedDiagnostics = MapClassDiagnosticCoordinator.Instance.Snapshot;
        _mapClassDiagnostics = _mapClasses.Select(mapClass =>
            completedDiagnostics.TryGetValue(mapClass, out var diagnostic)
                ? diagnostic
                : new MapClassDiagnostic(mapClass, true, [])).ToArray();
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow();
        Refresh(snapshot);

        var dpi = GetDpiForWindow(gameWindowHandle);
        var scale = Math.Max(1d, (dpi == 0 ? 96d : dpi) / 96d);
        var width = (int)Math.Round(360d * scale);
        var desiredHeight = ResolveDesiredHeight();
        var height = (int)Math.Round(desiredHeight * scale);
        var margin = (int)Math.Round(16d * scale);
        _window!.AppWindow.MoveAndResize(new RectInt32(
            (int)Math.Round(gameBounds.X + gameBounds.Width) - width - margin,
            (int)Math.Round(gameBounds.Y) + margin,
            width,
            height));
        GameInputPreservingWindow.Show(WindowNative.GetWindowHandle(_window));
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
            if (!_renderedMapClassDiagnostics.SequenceEqual(_mapClassDiagnostics))
                RebuildClassItems();
            var selected = _classComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is MapClassDiagnostic diagnostic
                    && string.Equals(diagnostic.MapClass, _pendingClass, StringComparison.Ordinal));
            if (!ReferenceEquals(_classComboBox.SelectedItem, selected))
                _classComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressClassSelectionChanged = false;
        }
        _classComboBox.IsEnabled = !snapshot.IsStarted;
        var selectedDiagnostic = _mapClassDiagnostics.FirstOrDefault(item =>
            string.Equals(item.MapClass, _pendingClass, StringComparison.OrdinalIgnoreCase));
        _classComboBox.BorderBrush = selectedDiagnostic?.IsHealthy is false
            ? new SolidColorBrush(Color.FromArgb(255, 255, 185, 0))
            : new SolidColorBrush(Color.FromArgb(255, 52, 59, 69));
        _classComboBox.Background = selectedDiagnostic?.IsHealthy is false
            ? new SolidColorBrush(Color.FromArgb(36, 255, 185, 0))
            : new SolidColorBrush(Color.FromArgb(255, 30, 35, 43));
        if (snapshot.IsStarted)
            SetSurveyToggle(snapshot.Mode == MapRunMode.Survey);
        else if (!_isSurveyModeAllowed())
            SetSurveyToggle(false);
        _surveyModeToggle.IsEnabled = CanChangeSurveyMode(snapshot);
        _beginButton.Visibility = snapshot.IsStarted
            ? Visibility.Collapsed
            : Visibility.Visible;
        _beginButton.IsEnabled = _pendingClass is not null;
        _beginButton.Content = _surveyModeToggle.IsOn ? "开始测绘" : "开始对局";
        _endButton.Visibility = snapshot.IsStarted
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshCorrectMapVisibility(snapshot);
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

    public void Hide()
    {
        if (_window is not null)
            GameInputPreservingWindow.Hide(WindowNative.GetWindowHandle(_window));
        _isVisible = false;
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        var root = new Border
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Color.FromArgb(255, 15, 20, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 62, 72, 86)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = BuildContent()
        };
        _window = new XamlWindow { Content = root, ExtendsContentIntoTitleBar = false };
        _window.Closed += (_, _) =>
        {
            _captureProtectionRegistration?.Dispose();
            _captureProtectionRegistration = null;
            _window = null;
            _isVisible = false;
        };
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        BorderlessWindowHelper.Apply(hwnd);
        GameInputPreservingWindow.Apply(hwnd);
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
            || _classComboBox.SelectedItem is not ComboBoxItem
                { Tag: MapClassDiagnostic diagnostic })
            return;
        var mapClass = diagnostic.MapClass;
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
            if (startSurvey && !_isSurveyModeAllowed())
            {
                SetSurveyToggle(false);
                _messageText.Text = "主设置未允许进入测绘模式，只能开始正常对局。";
                return;
            }

            // 无论测绘还是正常对局，点击开始后立即隐藏面板并将前台焦点还给游戏
            Hide();

            var begin = startSurvey
                ? _beginSurveyMatch
                : _beginMatch;
            await begin(mapClass);
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
}
/*
 * 文件职责：MapControlPanelWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
