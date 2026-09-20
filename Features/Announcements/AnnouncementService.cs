using System.Diagnostics;
using System.Text.Json;

namespace IDVBuff.Features.Announcements;

/// <summary>
/// 公告与知识库服务。
/// 提供完全异步、非阻塞、带超时熔断和本地持久化缓存的公告拉取与状态管理。
/// 确保即便在完全断网、弱网或服务端无响应时，也绝不阻塞主线程或导致程序卡死。
/// </summary>
public sealed class AnnouncementService
{
    private static readonly Lazy<AnnouncementService> LazyInstance = new(() => new AnnouncementService());
    public static AnnouncementService Instance => LazyInstance.Value;

    public static HttpClient? CustomHttpClient { get; set; }

    private static readonly HttpClient DefaultHttpClient = new()
    {
        BaseAddress = new Uri("https://community.idvb.xgflee.com/"),
        Timeout = TimeSpan.FromSeconds(4),
    };

    private static HttpClient Client => CustomHttpClient ?? DefaultHttpClient;

    private readonly string _storageDir;
    private readonly string _stateFilePath;
    private readonly string _cacheFilePath;
    private readonly Lock _stateLock = new();

    private AnnouncementLocalState _state = new();
    private List<AnnouncementItem> _cachedAnnouncements = [];
    private bool _stateLoaded;

    public event Action? UnreadCountChanged;

    public AnnouncementService(string? customStorageDir = null)
    {
        _storageDir = customStorageDir ?? Path.Combine(AppDataPaths.RootDirectory, "Announcements");
        _stateFilePath = Path.Combine(_storageDir, "announcements_state.json");
        _cacheFilePath = Path.Combine(_storageDir, "announcements_cache.json");
    }

    public async Task EnsureInitializedAsync()
    {
        if (_stateLoaded) return;
        await Task.Run(LoadStateAndCacheInternal).ConfigureAwait(false);
    }

    private void LoadStateAndCacheInternal()
    {
        lock (_stateLock)
        {
            if (_stateLoaded) return;
            try
            {
                if (!Directory.Exists(_storageDir))
                {
                    Directory.CreateDirectory(_storageDir);
                }

                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    _state = JsonSerializer.Deserialize<AnnouncementLocalState>(json) ?? new();
                }

                if (File.Exists(_cacheFilePath))
                {
                    var cacheJson = File.ReadAllText(_cacheFilePath);
                    var cache = JsonSerializer.Deserialize<AnnouncementResponse>(cacheJson);
                    if (cache?.Announcements != null)
                    {
                        _cachedAnnouncements = cache.Announcements;
                        UpdateItemReadStates(_cachedAnnouncements);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AnnouncementService] 加载本地缓存异常: {ex.Message}");
            }
            finally
            {
                _stateLoaded = true;
            }
        }
    }

    /// <summary>
    /// 同步返回当前已缓存的公告列表（零等待）。若尚未初始化，则立即从本地快速读取一次。
    /// </summary>
    public List<AnnouncementItem> GetCachedAnnouncements(string? category = null)
    {
        if (!_stateLoaded)
        {
            LoadStateAndCacheInternal();
        }
        lock (_stateLock)
        {
            return FilterByCategory(_cachedAnnouncements, category);
        }
    }

    public async Task<List<AnnouncementItem>> GetAnnouncementsAsync(string? category = null, bool forceRefresh = false)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        if (!forceRefresh && _cachedAnnouncements.Count > 0)
        {
            return FilterByCategory(_cachedAnnouncements, category);
        }

        return await Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var path = string.IsNullOrEmpty(category)
                    ? "api/announcements?limit=50"
                    : $"api/announcements?limit=50&category={Uri.EscapeDataString(category)}";

                using var response = await Client.GetAsync(path, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    var data = await JsonSerializer.DeserializeAsync<AnnouncementResponse>(stream, cancellationToken: cts.Token).ConfigureAwait(false);

                    if (data?.Announcements != null)
                    {
                        lock (_stateLock)
                        {
                            if (string.IsNullOrEmpty(category))
                            {
                                _cachedAnnouncements = data.Announcements;
                                _ = SaveCacheAsync(data);
                            }
                            UpdateItemReadStates(data.Announcements);
                        }

                        UnreadCountChanged?.Invoke();
                        return data.Announcements;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AnnouncementService] 拉取公告失败（已安全降级）: {ex.Message}");
            }

            lock (_stateLock)
            {
                return FilterByCategory(_cachedAnnouncements, category);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取需要在客户端启动后自动弹出的未读公告。
    /// <para>
    /// <see cref="AnnouncementItem.Priority"/> 是网页端“客户端启动弹窗提醒”的唯一映射；
    /// 分类、置顶状态和未读状态只影响列表与红点，不能隐式触发启动弹窗。
    /// </para>
    /// 调用方可要求跳过内存/磁盘缓存，确保启动检查不会永久停留在旧公告列表。
    /// </summary>
    public async Task<AnnouncementItem?> GetImportantUnreadAsync(bool forceRefresh = false)
    {
        var list = await GetAnnouncementsAsync(forceRefresh: forceRefresh).ConfigureAwait(false);
        lock (_stateLock)
        {
            return list.FirstOrDefault(item =>
                !item.IsRead &&
                item.Priority > 0);
        }
    }

    public int GetUnreadCount()
    {
        lock (_stateLock)
        {
            return _cachedAnnouncements.Count(item => !item.IsRead);
        }
    }

    public async Task MarkAsReadAsync(string announcementId)
    {
        if (string.IsNullOrEmpty(announcementId)) return;

        bool changed = false;
        lock (_stateLock)
        {
            if (_state.ReadIds.Add(announcementId))
            {
                changed = true;
                var item = _cachedAnnouncements.FirstOrDefault(a => a.Id == announcementId);
                if (item != null) item.IsRead = true;
            }
        }

        if (changed)
        {
            await SaveStateAsync().ConfigureAwait(false);
            UnreadCountChanged?.Invoke();
        }
    }

    private void UpdateItemReadStates(List<AnnouncementItem> items)
    {
        foreach (var item in items)
        {
            item.IsRead = _state.ReadIds.Contains(item.Id);
        }
    }

    private static List<AnnouncementItem> FilterByCategory(List<AnnouncementItem> items, string? category)
    {
        if (string.IsNullOrEmpty(category))
            return [.. items];

        return items.FindAll(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SaveStateAsync()
    {
        try
        {
            string json;
            lock (_stateLock)
            {
                json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(_stateFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementService] 保存状态异常: {ex.Message}");
        }
    }

    private async Task SaveCacheAsync(AnnouncementResponse data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cacheFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnnouncementService] 保存缓存异常: {ex.Message}");
        }
    }
}
