using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Small interactive match controller. This window is intentionally separate
/// from both the click-through map overlay and the full-screen manual selector.
/// </summary>
public sealed class MapControlPanelWindow : IDisposable
{
    private readonly Func<PlayerSlot, string, Task> _beginMatch;
    private readonly Func<Task<IReadOnlyList<string>>> _getMapClasses;
    private readonly Func<Task> _endMatch;
    private readonly Dictionary<PlayerSlot, Button> _slotButtons = [];
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
    private XamlWindow? _window;
    private PlayerSlot? _pendingSlot;
    private string? _pendingClass;
    private IReadOnlyList<string> _mapClasses = [];
    private MapMatchSnapshot _snapshot;
    private IntPtr _gameWindowHandle;
    private bool _isVisible;
    private bool _disposed;

    public MapControlPanelWindow(
        Func<PlayerSlot, string, Task> beginMatch,
        Func<Task<IReadOnlyList<string>>> getMapClasses,
        Func<Task> endMatch)
    {
        _beginMatch = beginMatch;
        _getMapClasses = getMapClasses;
        _endMatch = endMatch;
        _beginButton.Click += BeginButton_Click;
        _endButton.Click += EndButton_Click;
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
        _pendingClass = snapshot.MapClass is { } selected
            && _mapClasses.Any(name => string.Equals(
                name,
                selected,
                StringComparison.OrdinalIgnoreCase))
            ? _mapClasses.First(name => string.Equals(
                name,
                selected,
                StringComparison.OrdinalIgnoreCase))
            : _mapClasses[0];
        if (snapshot.IsStarted)
            _pendingSlot = snapshot.PlayerSlot;
        EnsureWindow();
        Refresh(snapshot);

        var dpi = GetDpiForWindow(gameWindowHandle);
        var scale = Math.Max(1d, (dpi == 0 ? 96d : dpi) / 96d);
        var width = (int)Math.Round(360d * scale);
        var height = (int)Math.Round(360d * scale);
        var margin = (int)Math.Round(16d * scale);
        _window!.AppWindow.MoveAndResize(new RectInt32(
            (int)Math.Round(gameBounds.X + gameBounds.Width) - width - margin,
            (int)Math.Round(gameBounds.Y) + margin,
            width,
            height));
        _window.Activate();
        _isVisible = true;
    }

    public void Refresh(MapMatchSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.IsStarted)
        {
            _pendingSlot = snapshot.PlayerSlot;
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
            ? $"对局状态：已开始 · 当前为 {(int)snapshot.PlayerSlot!.Value} 号玩家 · 模式 {_pendingClass}"
            : "对局状态：已结束";
        _classComboBox.ItemsSource = _mapClasses;
        _classComboBox.SelectedItem = _pendingClass;
        _classComboBox.IsEnabled = !snapshot.IsStarted;
        foreach (var (slot, button) in _slotButtons)
        {
            button.IsEnabled = !snapshot.IsStarted;
            button.BorderThickness = _pendingSlot == slot
                ? new Thickness(3)
                : new Thickness(1);
            button.BorderBrush = new SolidColorBrush(
                _pendingSlot == slot
                    ? Color.FromArgb(255, 91, 176, 255)
                    : Color.FromArgb(255, 72, 80, 92));
        }
        _beginButton.Visibility = snapshot.IsStarted
            ? Visibility.Collapsed
            : Visibility.Visible;
        _beginButton.IsEnabled = _pendingSlot is not null
            && _pendingClass is not null;
        _endButton.Visibility = snapshot.IsStarted
            ? Visibility.Visible
            : Visibility.Collapsed;
        _messageText.Text = snapshot.IsStarted
            ? "结束后将清空本局地图和玩家状态。"
            : _pendingSlot is null
                ? "请选择本局自己的玩家序号。"
                : $"已选择 {(int)_pendingSlot.Value} 号玩家 · 模式 {_pendingClass}，可以开始对局。";
    }

    public void Reset(MapMatchSnapshot snapshot)
    {
        _pendingSlot = null;
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
        content.Children.Add(new TextBlock
        {
            Text = "本局玩家序号",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 174, 184, 198))
        });

        var slots = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < 4; index++)
        {
            slots.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }
        foreach (var slot in MapPlayerAssetCatalog.Slots)
        {
            var button = new Button
            {
                Height = 68,
                Padding = new Thickness(8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(
                    Color.FromArgb(255, 29, 36, 47)),
                CornerRadius = new CornerRadius(8),
                Content = new Image
                {
                    Width = 48,
                    Height = 48,
                    Stretch = Stretch.Uniform,
                    Source = new BitmapImage(
                        new Uri(MapPlayerAssetCatalog.ResolvePath(slot)))
                },
                Tag = slot
            };
            button.Click += SlotButton_Click;
            _slotButtons.Add(slot, button);
            Grid.SetColumn(button, (int)slot - 1);
            slots.Children.Add(button);
        }
        content.Children.Add(slots);
        content.Children.Add(_messageText);
        content.Children.Add(_beginButton);
        content.Children.Add(_endButton);
        return content;
    }

    private void SlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.IsStarted || sender is not Button { Tag: PlayerSlot slot })
            return;
        _pendingSlot = slot;
        Refresh(_snapshot);
    }

    private void ClassComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_snapshot.IsStarted || _classComboBox.SelectedItem is not string mapClass)
            return;
        _pendingClass = mapClass;
        Refresh(_snapshot);
    }

    private async void BeginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSlot is not { } slot || _pendingClass is not { } mapClass)
            return;
        SetActionsEnabled(false);
        try
        {
            await _beginMatch(slot, mapClass);
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
            await _endMatch();
            _pendingSlot = null;
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

    private void SetActionsEnabled(bool enabled)
    {
        _beginButton.IsEnabled = enabled
            && _pendingSlot is not null
            && _pendingClass is not null;
        _endButton.IsEnabled = enabled;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
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
