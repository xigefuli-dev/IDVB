using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using IDVBuff.Features.Announcements;

namespace IDVBuff.Views;

/// <summary>
/// 独立沉浸式大尺寸公告与冷知识窗口（主逻辑与生命周期）。
/// 动态适配多种屏幕分辨率与 DPI，支持 GitHub README 级别的富文本、复杂表格、动态 GIF 和图片展示，
/// 并无缝契合浅色与深色主题。完全异步加载，绝对不阻塞主线程。
/// </summary>
public sealed partial class AnnouncementWindow
{
    private const uint WmNcHitTest = 0x0084;
    private const uint WmSysCommand = 0x0112;
    private const int HtCaption = 2;
    private const int HtClient = 1;
    private const int ScMove = 0xF010;
    private const int SysCommandMask = 0xFFF0;
    private const nuint NonMovableWindowSubclassId = 0x49445642;

    private static AnnouncementWindow? _currentInstance;
    private static readonly WindowSubclassProcedure NonMovableWindowProcedure =
        PreventWindowMove;

    private delegate IntPtr WindowSubclassProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        WindowSubclassProcedure procedure,
        nuint subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window,
        WindowSubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly NativeMarkdownView _markdownView;
    private readonly ListView _announcementsListView;
    private readonly ProgressRing _loadingRing;
    private readonly TextBlock _emptyTextBlock;
    private readonly TextBlock _detailTitleBlock;
    private readonly TextBlock _detailMetaBlock;
    private readonly Border _categoryBadge;
    private readonly TextBlock _categoryBadgeText;

    private List<AnnouncementItem> _allAnnouncements = [];
    private List<AnnouncementItem> _filteredAnnouncements = [];
    private AnnouncementItem? _selectedItem;
    private readonly Border _themeRoot = new();
    private IDVBuff.Presentation.Theming.ThemeScope? _themeScope;

    private AnnouncementWindow()
    {
        _window = new Window { Title = "消息通知" };
        _appWindow = _window.AppWindow;

        // 窗口无边框无标题栏，内容完全顶格填充
        _window.ExtendsContentIntoTitleBar = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 公告是启动后的固定提醒窗口：保持在最前，且不提供拖动或调整大小入口。
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        IDVBuff.Features.Maps.BorderlessWindowHelper.Apply(hWnd);
        if (!SetWindowSubclass(
                hWnd,
                NonMovableWindowProcedure,
                NonMovableWindowSubclassId,
                UIntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"无法安装公告窗口防移动处理（Win32 {Marshal.GetLastWin32Error()}）。");
        }

        _appWindow.Closing += (sender, args) =>
        {
            args.Cancel = true;
            _appWindow.Hide();
        };

        _markdownView = new NativeMarkdownView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _announcementsListView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0, 4, 0, 8),
        };
        _announcementsListView.SelectionChanged += AnnouncementsListView_SelectionChanged;

        _loadingRing = new ProgressRing
        {
            IsActive = false,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _emptyTextBlock = new TextBlock
        {
            Text = "暂无消息通知",
            FontSize = 14,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _detailTitleBlock = new TextBlock
        {
            FontSize = 23,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorPrimaryBrush"),
        };

        _detailMetaBlock = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorSecondaryBrush"),
        };

        _categoryBadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };

        _categoryBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = FluentTheme.Brush(_themeRoot, "AccentFillColorDefaultBrush"),
            Child = _categoryBadgeText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _window.Content = BuildLayout();
        _themeScope = IDVBuff.Presentation.Theming.ThemeService.AttachWindow(_window, _themeRoot);
        _window.Closed += (_, _) =>
        {
            RemoveWindowSubclass(hWnd, NonMovableWindowProcedure, NonMovableWindowSubclassId);
            if (_currentInstance == this) _currentInstance = null;
        };

        // 优先同步直出已缓存数据，实现零延迟、进门即见内容
        var cached = AnnouncementService.Instance.GetCachedAnnouncements();
        if (cached.Count > 0)
        {
            _allAnnouncements = cached;
            ApplyFilter();
        }

        PlaceAndSizeWindow();
    }

    private static IntPtr PreventWindowMove(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        UIntPtr referenceData)
    {
        // AppWindow can still report a caption hit after its title bar has been hidden.
        // Convert that hit to client content and also reject keyboard/system-menu moves.
        if (message == WmSysCommand
            && ((long)wParam & SysCommandMask) == ScMove)
        {
            return IntPtr.Zero;
        }

        var result = DefSubclassProc(window, message, wParam, lParam);
        return message == WmNcHitTest && result == new IntPtr(HtCaption)
            ? new IntPtr(HtClient)
            : result;
    }

    /// <summary>
    /// 显示公告大窗口。若窗口已存在，则激活并置顶；支持跳转至指定公告。
    /// 包含严密异常隔离，杜绝因窗口创建或弹窗异常导致主程序暴毙。
    /// </summary>
    public static void Show(string? targetAnnouncementId = null)
    {
        try
        {
            if (_currentInstance != null)
            {
                _currentInstance._appWindow.Show();
                _currentInstance._window.Activate();
                if (!string.IsNullOrEmpty(targetAnnouncementId))
                {
                    _currentInstance.SelectAnnouncementById(targetAnnouncementId);
                }
                // 先保留当前内容，再刷新远端，避免旧缓存阻止新公告显示。
                _currentInstance.LoadDataAsync(targetAnnouncementId, forceRefresh: true, silent: true);
                return;
            }

            var instance = new AnnouncementWindow();
            _currentInstance = instance;
            instance._appWindow.Show();
            instance._window.Activate();
            // 构造函数已同步展示缓存；随后总是请求远端以合并最新公告。
            instance.LoadDataAsync(
                targetAnnouncementId,
                forceRefresh: true,
                silent: instance._allAnnouncements.Count > 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementWindow] 显示公告窗口异常（已安全拦截）: {ex.Message}");
        }
    }

    private void PlaceAndSizeWindow()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            int width = Math.Clamp((int)(workArea.Width * 0.70), 860, 1160);
            int height = Math.Clamp((int)(workArea.Height * 0.60), 520, 680);

            if (width > workArea.Width) width = (int)(workArea.Width * 0.95);
            if (height > workArea.Height) height = (int)(workArea.Height * 0.95);

            int x = workArea.X + (workArea.Width - width) / 2;
            int y = workArea.Y + (workArea.Height - height) / 2;

            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementWindow] 计算窗口尺寸异常: {ex.Message}");
        }
    }

    private async void LoadDataAsync(string? selectId = null, bool forceRefresh = false, bool silent = false)
    {
        if (!silent && _allAnnouncements.Count == 0)
        {
            _loadingRing.IsActive = true;
            _loadingRing.Visibility = Visibility.Visible;
            _emptyTextBlock.Visibility = Visibility.Collapsed;
        }

        var items = await AnnouncementService.Instance.GetAnnouncementsAsync(forceRefresh: forceRefresh);

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _loadingRing.IsActive = false;
            _loadingRing.Visibility = Visibility.Collapsed;
            _allAnnouncements = items;
            ApplyFilter();

            if (!string.IsNullOrEmpty(selectId))
            {
                SelectAnnouncementById(selectId);
            }
            else if (_filteredAnnouncements.Count > 0 && _announcementsListView.SelectedIndex < 0)
            {
                _announcementsListView.SelectedIndex = 0;
            }
        });
    }

    private void ApplyFilter()
    {
        _filteredAnnouncements = [.. _allAnnouncements];

        _announcementsListView.Items.Clear();
        foreach (var item in _filteredAnnouncements)
        {
            _announcementsListView.Items.Add(CreateAnnouncementListItem(item));
        }

        _emptyTextBlock.Visibility = _filteredAnnouncements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_filteredAnnouncements.Count > 0)
        {
            _announcementsListView.SelectedIndex = 0;
        }
        else
        {
            ClearDetail();
        }
    }

    private void AnnouncementsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_announcementsListView.SelectedItem is ListViewItem lvi && lvi.Tag is AnnouncementItem item)
        {
            _selectedItem = item;
            ShowDetail(item);

            if (!item.IsRead)
            {
                item.IsRead = true;
                _ = AnnouncementService.Instance.MarkAsReadAsync(item.Id);
                if (lvi.Content is Grid g && g.Children.Count > 0 && g.Children[0] is Border dot)
                {
                    dot.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void ShowDetail(AnnouncementItem item)
    {
        _detailTitleBlock.Text = item.Title;
        _categoryBadgeText.Text = AnnouncementCategories.GetDisplayName(item.Category);
        _categoryBadge.Background = GetCategoryBrush(item.Category);

        _categoryBadge.Visibility = Visibility.Visible;
        var meta = FormatDate(item.PublishAt);
        if (!string.IsNullOrEmpty(item.AuthorName)) meta += $"  ·  {item.AuthorName}";
        if (!string.IsNullOrEmpty(item.Tag)) meta += $"  ·  {item.Tag}";
        _detailMetaBlock.Text = meta;

        RenderCurrentItemMarkdown();
    }

    private void RenderCurrentItemMarkdown()
    {
        if (_selectedItem == null)
        {
            _markdownView.Clear();
            return;
        }

        try
        {
            _markdownView.SetMarkdown(_selectedItem.Content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementWindow] 渲染 Markdown 异常: {ex.Message}");
        }
    }

    private void ClearDetail()
    {
        _selectedItem = null;
        _detailTitleBlock.Text = string.Empty;
        _detailMetaBlock.Text = string.Empty;
        _categoryBadge.Visibility = Visibility.Collapsed;
        _markdownView.Clear();
    }

    private void SelectAnnouncementById(string id)
    {
        for (int i = 0; i < _filteredAnnouncements.Count; i++)
        {
            if (_filteredAnnouncements[i].Id == id)
            {
                _announcementsListView.SelectedIndex = i;
                return;
            }
        }
    }

    private static Brush GetCategoryBrush(string? category) => category switch
    {
        AnnouncementCategories.Update => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 79, 70, 229)),
        AnnouncementCategories.Tips => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 16, 185, 129)),
        AnnouncementCategories.Notice => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 107, 114, 128)),
        _ => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 59, 130, 246))
    };

    private static string FormatDate(string? dateString)
    {
        if (DateTimeOffset.TryParse(dateString, out var dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        return dateString ?? string.Empty;
    }
}
