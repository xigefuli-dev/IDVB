using System.Diagnostics;
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
    private static AnnouncementWindow? _currentInstance;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly WebView2 _webView;
    private readonly ListView _announcementsListView;
    private readonly ProgressRing _loadingRing;
    private readonly TextBlock _emptyTextBlock;
    private readonly TextBlock _detailTitleBlock;
    private readonly TextBlock _detailMetaBlock;
    private readonly Border _categoryBadge;
    private readonly TextBlock _categoryBadgeText;
    private readonly CheckBox _dismissCheckBox;

    private List<AnnouncementItem> _allAnnouncements = [];
    private List<AnnouncementItem> _filteredAnnouncements = [];
    private AnnouncementItem? _selectedItem;
    private string? _currentCategory;
    private bool _webViewReady;

    private AnnouncementWindow()
    {
        _window = new Window { Title = "消息通知" };
        _appWindow = _window.AppWindow;

        // 窗口无边框无标题栏，内容完全顶格填充
        _window.ExtendsContentIntoTitleBar = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        IDVBuff.Features.Maps.BorderlessWindowHelper.Apply(hWnd);

        _appWindow.Closing += (sender, args) =>
        {
            args.Cancel = true;
            _appWindow.Hide();
        };

        _webView = new WebView2
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
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _detailTitleBlock = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
        };

        _detailMetaBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
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
            Background = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            Child = _categoryBadgeText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _dismissCheckBox = new CheckBox
        {
            Content = "不再自动提示此条通知",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dismissCheckBox.Click += DismissCheckBox_Click;

        _window.Content = BuildLayout();
        _window.Closed += (_, _) =>
        {
            if (_currentInstance == this) _currentInstance = null;
        };

        // 优先同步直出已缓存数据，实现零延迟、进门即见内容
        var cached = AnnouncementService.Instance.GetCachedAnnouncements();
        if (cached.Count > 0)
        {
            _allAnnouncements = cached;
            ApplyFilter();
        }

        InitializeWebView();
        PlaceAndSizeWindow();
    }

    /// <summary>
    /// 显示公告大窗口。若窗口已存在，则激活并置顶；支持跳转至指定公告。
    /// </summary>
    public static void Show(string? targetAnnouncementId = null)
    {
        if (_currentInstance != null)
        {
            _currentInstance._appWindow.Show();
            _currentInstance._window.Activate();
            if (!string.IsNullOrEmpty(targetAnnouncementId))
            {
                _currentInstance.SelectAnnouncementById(targetAnnouncementId);
            }
            _currentInstance.LoadDataAsync(targetAnnouncementId, silent: true);
            return;
        }

        var instance = new AnnouncementWindow();
        _currentInstance = instance;
        instance._appWindow.Show();
        instance._window.Activate();
        instance.LoadDataAsync(targetAnnouncementId, silent: instance._allAnnouncements.Count > 0);
    }

    private async void InitializeWebView()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Markdown", "template.html");
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(htmlPath);
            }

            _webView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "ready")
                        {
                            _webViewReady = true;
                            UpdateWebViewTheme();
                            if (_selectedItem != null)
                            {
                                RenderCurrentItemMarkdown();
                            }
                        }
                        else if (type == "open_url" && root.TryGetProperty("url", out var urlProp))
                        {
                            var url = urlProp.GetString();
                            if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
                            {
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AnnouncementWindow] 处理 WebView 消息异常: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementWindow] 初始化 WebView2 异常: {ex.Message}");
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

            int width = Math.Clamp((int)(workArea.Width * 0.65), 860, 1160);
            int height = Math.Clamp((int)(workArea.Height * 0.70), 560, 760);

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
        if (string.IsNullOrEmpty(_currentCategory))
        {
            _filteredAnnouncements = [.. _allAnnouncements];
        }
        else
        {
            _filteredAnnouncements = _allAnnouncements
                .Where(a => string.Equals(a.Category, _currentCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

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

        var meta = $"发布时间：{FormatDate(item.PublishAt)}";
        if (!string.IsNullOrEmpty(item.AuthorName)) meta += $"  ·  作者：{item.AuthorName}";
        if (!string.IsNullOrEmpty(item.Tag)) meta += $"  ·  标签：{item.Tag}";
        _detailMetaBlock.Text = meta;

        _dismissCheckBox.IsChecked = item.IsDismissed;
        _dismissCheckBox.Visibility = item.Priority > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderCurrentItemMarkdown();
    }

    private void RenderCurrentItemMarkdown()
    {
        if (_selectedItem == null || !_webViewReady) return;

        try
        {
            var contentJson = JsonSerializer.Serialize(_selectedItem.Content);
            _webView.ExecuteScriptAsync($"window.setMarkdown({contentJson});").AsTask();
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
        _dismissCheckBox.Visibility = Visibility.Collapsed;
        if (_webViewReady)
        {
            _webView.ExecuteScriptAsync("window.setMarkdown('');").AsTask();
        }
    }

    private void DismissCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        bool isDismissed = _dismissCheckBox.IsChecked == true;
        _selectedItem.IsDismissed = isDismissed;
        if (isDismissed)
        {
            _ = AnnouncementService.Instance.DismissPopupAsync(_selectedItem.Id);
        }
    }

    private void UpdateWebViewTheme()
    {
        if (!_webViewReady) return;
        try
        {
            var isDark = ((FrameworkElement)_window.Content).ActualTheme == ElementTheme.Dark;
            var themeName = isDark ? "dark" : "light";
            _webView.ExecuteScriptAsync($"window.setTheme('{themeName}');").AsTask();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementWindow] 切换 WebView 主题异常: {ex.Message}");
        }
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
