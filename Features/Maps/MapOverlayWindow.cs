using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Owns the state of the non-activating, click-through runtime overlay.
/// Rendering is delegated to a native layered window so the game receives
/// mouse input through every transparent and painted pixel.
/// </summary>
public sealed class MapOverlayWindow : IDisposable
{
    private readonly MapOverlayNativeWindow _nativeWindow;
    private MapOverlayRenderMap? _map;
    private MapOverlayRenderPlayer? _player;
    private MapOverlayRenderMap? _persistentMiniMap;
    private Bitmap? _lockedBackground;
    private int _lockedBackgroundWidth;
    private int _lockedBackgroundHeight;
    private uint _lockedBackgroundDpi;
    private MapOverlayStatus? _status;
    private IntPtr _gameWindowHandle;
    private MapScreenRect _gameBounds;
    private bool _showStatusPreference = true;
    private bool _reverseAlternation;
    private bool _allowExtend;
    private bool _showGateMarkers = true;
    private bool _showAuxiliaryAnchors = true;
    private bool _showTextAnnotations = true;
    private bool _showBoxAnnotations = true;
    private bool _showLineAnnotations = true;
    private bool _showGateMarkersOnMiniMap = true;
    private bool _showAuxiliaryAnchorsOnMiniMap = true;
    private bool _showTextAnnotationsOnMiniMap = true;
    private bool _showBoxAnnotationsOnMiniMap = true;
    private bool _showLineAnnotationsOnMiniMap = true;
    private bool _showFloorOnMiniMap;
    private float _mapOpacity = 0.46f;
    private float _statusOpacity = 1f;
    private float _statusOffsetX;
    private float _statusOffsetY;
    private float _miniMapOpacity = 0.55f;
    private float _miniMapOffsetX;
    private float _miniMapOffsetY = 50f;
    private double? _miniMapScale;
    private double? _miniMapBaseScale;
    private string? _miniMapImageKey;
    private readonly Dictionary<string, double> _temporaryMiniMapScales =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _showMainContent = true;
    private int _presentDepth;
    private bool _presentDirty;
    private int _presentCount;
    private bool _disposed;

    public MapOverlayWindow(ICaptureProtectionService? captureProtection = null)
    {
        _nativeWindow = new MapOverlayNativeWindow(captureProtection);
    }

    public bool IsCaptureExclusionEnabled => _nativeWindow.IsCaptureExclusionEnabled;

    public bool IsVisible => _nativeWindow.IsVisible;
    public bool HasMap => _map is not null;
    public int PresentCount => Volatile.Read(ref _presentCount);
    public double? CurrentMiniMapScale => _persistentMiniMap is not null ? _miniMapScale : null;
    public bool HasStatus => _status is not null;
    private bool HasContent => HasMap || HasStatus || _persistentMiniMap is not null;

    public void UpdateStatus(
        MapOverlayStatus status,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool showImmediately = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!gameBounds.IsValid || gameWindowHandle == IntPtr.Zero)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "状态层更新被跳过：游戏窗口边界或句柄无效。");
            return;
        }

        _gameBounds = gameBounds;
        _gameWindowHandle = gameWindowHandle;
        _showStatusPreference = showStatusPreference;
        _status = status;
        if (showImmediately)
            Present();
    }

    public void ClearStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _status = null;
        RefreshVisibleContent();
    }

    public void UpdateMap(
        RuntimeMapRecognition recognition,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        MapScreenRect? viewportBounds = null,
        bool preservePlayer = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (recognition.Result.OverlayTransform is not { } transform
            || !File.Exists(recognition.FloorImagePath)
            || !gameBounds.IsValid
            || gameWindowHandle == IntPtr.Zero)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "地图层更新被跳过：变换、地图图像、游戏边界或窗口句柄无效。",
                details: new()
                {
                    ["hasTransform"] = recognition.Result.OverlayTransform is not null,
                    ["floorImagePath"] = recognition.FloorImagePath,
                    ["imageExists"] = File.Exists(recognition.FloorImagePath),
                    ["boundsValid"] = gameBounds.IsValid,
                    ["windowHandle"] = $"0x{gameWindowHandle.ToInt64():X}"
                });
            return;
        }

        _gameBounds = gameBounds;
        _gameWindowHandle = gameWindowHandle;
        _showStatusPreference = showStatusPreference;
        var overlayWidth = transform.ReferenceWidth * transform.ScaleX;
        var overlayHeight = transform.ReferenceHeight * transform.ScaleY;
        if (!double.IsFinite(overlayWidth)
            || !double.IsFinite(overlayHeight)
            || overlayWidth <= 0
            || overlayHeight <= 0)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "地图层更新被跳过：Overlay 尺寸无效。",
                details: new()
                {
                    ["overlayWidth"] = overlayWidth,
                    ["overlayHeight"] = overlayHeight
                });
            return;
        }

        var profile = recognition.Map.Recognition.GetFloor(recognition.Result.Floor)
            ?? recognition.Map.Recognition.FirstFloor;
        var anchors = profile.Anchors
            .Where(anchor => anchor.Bounds?.IsValid is true)
            .Select(anchor => new MapOverlayRenderAnchor(
                anchor.Key,
                anchor.DisplayName,
                anchor.Bounds!.Clone()))
            .ToArray();
        var annotations = profile.Annotations
            .Where(a => a.IsValid)
            .Select(a => new MapOverlayRenderAnnotation(
                a.Type,
                a.ColorIndex,
                a.EffectiveColorHex,
                a.Bounds?.Clone(),
                a.Start?.Clone(),
                a.End?.Clone(),
                a.Text,
                a.FontFamily,
                a.FontSize,
                a.IsBold,
                a.IsItalic,
                a.IsStrikethrough))
            .ToArray();
        _map = new MapOverlayRenderMap(
            recognition.FloorImagePath,
            ToFiniteSingle(transform.OffsetX - gameBounds.X),
            ToFiniteSingle(transform.OffsetY - gameBounds.Y),
            ToFiniteSingle(overlayWidth),
            ToFiniteSingle(overlayHeight),
            anchors,
            viewportBounds is { IsValid: true } viewport
                ? new MapScreenRect(
                    viewport.X - gameBounds.X,
                    viewport.Y - gameBounds.Y,
                    viewport.Width,
                    viewport.Height)
                : new MapScreenRect(
                    0d,
                    0d,
                    gameBounds.Width,
                    gameBounds.Height))
        {
            Annotations = annotations
        };
        if (_persistentMiniMap is not null)
        {
            _persistentMiniMap = _persistentMiniMap with
            {
                Anchors = anchors,
                Annotations = annotations
            };
        }
        InvalidateLockedBackground();
        if (!preservePlayer)
            _player = null;
        Present();
    }

    public void LockBackground(
        RuntimeMapRecognition recognition,
        MapScreenRect viewportBounds,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool preservePlayer = false) =>
        UpdateMap(
            recognition,
            gameBounds,
            gameWindowHandle,
            showStatusPreference,
            viewportBounds,
            preservePlayer);

    public void UpdateMapTransform(
        MapOverlayTransform transform,
        bool preservePlayer = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_map is null)
            return;
        var overlayWidth = transform.ReferenceWidth * transform.ScaleX;
        var overlayHeight = transform.ReferenceHeight * transform.ScaleY;
        if (!double.IsFinite(overlayWidth)
            || !double.IsFinite(overlayHeight)
            || !double.IsFinite(transform.OffsetX)
            || !double.IsFinite(transform.OffsetY)
            || overlayWidth <= 0
            || overlayHeight <= 0)
        {
            return;
        }

        _map = _map with
        {
            Left = ToFiniteSingle(transform.OffsetX - _gameBounds.X),
            Top = ToFiniteSingle(transform.OffsetY - _gameBounds.Y),
            Width = ToFiniteSingle(overlayWidth),
            Height = ToFiniteSingle(overlayHeight)
        };
        InvalidateLockedBackground();
        if (!preservePlayer)
            _player = null;
        if (IsVisible)
            Present();
    }

    public bool TrySetCaptureExclusion(bool enabled, out string failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _nativeWindow.TrySetCaptureExclusion(enabled, out failureReason);
    }

    public void UpdatePlayer(MapPlayerState? player)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_map is null)
            return;
        _player = player?.IsTrusted is true
            ? new MapOverlayRenderPlayer(
                player.PlayerSlot,
                MapPlayerAssetCatalog.ResolvePath(player.PlayerSlot),
                ToFiniteSingle(player.ScreenPoint.X - _gameBounds.X),
                ToFiniteSingle(player.ScreenPoint.Y - _gameBounds.Y),
                ToFiniteSingle(player.MarkerWidth),
                ToFiniteSingle(player.MarkerHeight),
                ToFiniteSingle(player.Confidence))
            : null;
        if (IsVisible)
            Present();
    }

    public void ClearSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _map = null;
        _player = null;
        InvalidateLockedBackground();
        RefreshVisibleContent();
    }

    public void ClearMap()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _map = null;
        _player = null;
        InvalidateLockedBackground();
        RefreshVisibleContent();
    }

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasContent)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                "Overlay Toggle 被忽略：当前没有地图、状态或小地图内容。");
            return;
        }
        if (IsVisible)
            Hide();
        else
            Show();
    }

    public void SetStatusVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _showStatusPreference = visible;
        RefreshVisibleContent();
    }

    public void SetReverseAlternateDisplay(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reverseAlternation = enabled;
        if (IsVisible)
            Present();
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _map = null;
        _player = null;
        _persistentMiniMap = null;
        _miniMapScale = null;
        _miniMapBaseScale = null;
        _miniMapImageKey = null;
        _showMainContent = true;
        InvalidateLockedBackground();
        _status = null;
        _gameWindowHandle = IntPtr.Zero;
        _gameBounds = default;
        Hide();
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasContent)
            return;
        Present();
    }

    public void Hide() => _nativeWindow.Hide();

    public IDisposable DeferPresent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        checked { _presentDepth++; }
        return new PresentLease(this);
    }

    public void SetMainContentVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_showMainContent == visible)
            return;
        _showMainContent = visible;
        if (IsVisible)
            Present();
    }

    private void RefreshVisibleContent()
    {
        if (!IsVisible)
            return;
        if (!HasContent)
            Hide();
        else
            Present();
    }

    private void Present()
    {
        if (Volatile.Read(ref _presentDepth) > 0)
        {
            _presentDirty = true;
            return;
        }

        PresentCore();
    }

    private void PresentCore()
    {
        if (!_gameBounds.IsValid || _gameWindowHandle == IntPtr.Zero)
            return;

        var foregroundBeforeShow = GetForegroundWindow();
        var pixelWidth = Math.Max(1, (int)Math.Round(_gameBounds.Width));
        var pixelHeight = Math.Max(1, (int)Math.Round(_gameBounds.Height));
        var dpi = GetDpiForWindow(_gameWindowHandle);
        if (dpi == 0)
            dpi = 96;
        var monitorWorkingArea = _nativeWindow.GetMonitorWorkingArea(_gameWindowHandle);
        var showStatus = _status is not null;
        if (showStatus && !_showStatusPreference)
        {
            showStatus = _reverseAlternation
                ? _map is not null
                : _map is null;
        }

        var scene = new MapOverlayRenderScene(
            pixelWidth,
            pixelHeight,
            dpi,
            _showMainContent ? _map : null,
            _showMainContent ? _status : null,
            _showMainContent && showStatus,
            _showMainContent ? _player : null,
            MiniMap: _persistentMiniMap,
            AllowMapExtendBeyondBounds: _allowExtend,
            GameScreenBounds: _gameBounds,
            MonitorWorkingArea: monitorWorkingArea,
            ShowGateMarkers: _showGateMarkers,
            ShowAuxiliaryAnchors: _showAuxiliaryAnchors,
            ShowTextAnnotations: _showTextAnnotations,
            ShowBoxAnnotations: _showBoxAnnotations,
            ShowLineAnnotations: _showLineAnnotations,
            ShowGateMarkersOnMiniMap: _showGateMarkersOnMiniMap,
            ShowAuxiliaryAnchorsOnMiniMap: _showAuxiliaryAnchorsOnMiniMap,
            ShowTextAnnotationsOnMiniMap: _showTextAnnotationsOnMiniMap,
            ShowBoxAnnotationsOnMiniMap: _showBoxAnnotationsOnMiniMap,
            ShowLineAnnotationsOnMiniMap: _showLineAnnotationsOnMiniMap,
            MapOpacity: _mapOpacity,
            StatusOpacity: _statusOpacity,
            StatusOffsetX: _statusOffsetX,
            StatusOffsetY: _statusOffsetY,
            MiniMapOpacity: _miniMapOpacity,
            MiniMapOffsetX: _miniMapOffsetX,
            MiniMapOffsetY: _miniMapOffsetY,
            ShowFloorOnMiniMap: _showFloorOnMiniMap);

        try
        {
            using var bitmap = RenderScene(scene);
            _nativeWindow.Present(bitmap, _gameBounds);
            Interlocked.Increment(ref _presentCount);
        }
        catch (Exception ex)
        {
            _nativeWindow.Hide();
            Debug.WriteLine($"[Overlay] Present 异常: {ex.Message}");
            throw;
        }

        if (foregroundBeforeShow == _gameWindowHandle
            && GetForegroundWindow() != _gameWindowHandle)
        {
            SetForegroundWindow(_gameWindowHandle);
            if (GetForegroundWindow() != _gameWindowHandle)
            {
                Hide();
                throw new InvalidOperationException(
                    "图层窗口意外取得了输入焦点，已自动隐藏以恢复游戏操作。");
            }
        }
    }

    private sealed class PresentLease(MapOverlayWindow owner) : IDisposable
    {
        private MapOverlayWindow? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndPresentDeferral();
        }
    }

    private void EndPresentDeferral()
    {
        if (_presentDepth <= 0)
            return;
        _presentDepth--;
        if (_presentDepth != 0 || !_presentDirty)
            return;
        _presentDirty = false;
        PresentCore();
    }

    private static float ToFiniteSingle(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Overlay geometry is outside the supported range.");
        return (float)value;
    }

    private Bitmap RenderScene(MapOverlayRenderScene scene)
    {
        if (scene.Map is null)
            return MapOverlayBitmapRenderer.Render(scene);
        if (_lockedBackground is null
            || _lockedBackgroundWidth != scene.PixelWidth
            || _lockedBackgroundHeight != scene.PixelHeight
            || _lockedBackgroundDpi != scene.Dpi)
        {
            InvalidateLockedBackground();
            _lockedBackground = MapOverlayBitmapRenderer.Render(
                scene with
                {
                    Status = null,
                    ShowStatus = false,
                    Player = null,
                    MiniMap = null
                });
            _lockedBackgroundWidth = scene.PixelWidth;
            _lockedBackgroundHeight = scene.PixelHeight;
            _lockedBackgroundDpi = scene.Dpi;
        }
        return MapOverlayBitmapRenderer.ComposeDynamic(
            _lockedBackground,
            scene);
    }

    private void InvalidateLockedBackground()
    {
        _lockedBackground?.Dispose();
        _lockedBackground = null;
        _lockedBackgroundWidth = 0;
        _lockedBackgroundHeight = 0;
        _lockedBackgroundDpi = 0;
    }

    public void SetAllowExtend(bool allow)
    {
        _allowExtend = allow;
        if (IsVisible)
            Present();
    }

    public void SetMapOpacity(double opacity)
    {
        _mapOpacity = (float)opacity;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowGateMarkers(bool show)
    {
        _showGateMarkers = show;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowAuxiliaryAnchors(bool show)
    {
        _showAuxiliaryAnchors = show;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowTextAnnotations(bool show)
    {
        _showTextAnnotations = show;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowBoxAnnotations(bool show)
    {
        _showBoxAnnotations = show;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowLineAnnotations(bool show)
    {
        _showLineAnnotations = show;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void SetShowGateMarkersOnMiniMap(bool show)
    {
        _showGateMarkersOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowAuxiliaryAnchorsOnMiniMap(bool show)
    {
        _showAuxiliaryAnchorsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowTextAnnotationsOnMiniMap(bool show)
    {
        _showTextAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowBoxAnnotationsOnMiniMap(bool show)
    {
        _showBoxAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowLineAnnotationsOnMiniMap(bool show)
    {
        _showLineAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowFloorOnMiniMap(bool show)
    {
        _showFloorOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetStatusOpacity(double opacity)
    {
        _statusOpacity = (float)opacity;
        if (IsVisible)
            Present();
    }

    public void SetStatusOffsetX(double offsetX)
    {
        _statusOffsetX = (float)offsetX;
        if (IsVisible)
            Present();
    }

    public void SetStatusOffsetY(double offsetY)
    {
        _statusOffsetY = (float)offsetY;
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOpacity(double opacity)
    {
        _miniMapOpacity = (float)opacity;
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOffsetX(double offsetX)
    {
        _miniMapOffsetX = (float)offsetX;
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOffsetY(double offsetY)
    {
        _miniMapOffsetY = (float)offsetY;
        if (IsVisible)
            Present();
    }

    public void SetMiniMapScale(double scale)
    {
        if (_persistentMiniMap is not { } miniMap) return;
        if (!MapOverlayBitmapRenderer.TryGetScaledImageSize(
                miniMap.ImagePath, scale, out var w, out var h))
            return;
        _persistentMiniMap = miniMap with { Width = w, Height = h };
        _miniMapScale = scale;
        if (_miniMapImageKey is not null)
            _temporaryMiniMapScales[_miniMapImageKey] = scale;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    internal void SetPersistentMiniMapState(
        string imagePath,
        MapOverlayTransform transform,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        double miniMapScale,
        IReadOnlyList<MapOverlayRenderAnchor>? anchors = null,
        IReadOnlyList<MapOverlayRenderAnnotation>? annotations = null,
        string? floorLabel = null)
    {
        _gameBounds = gameBounds;
        _gameWindowHandle = gameWindowHandle;
        var imageKey = Path.GetFullPath(imagePath);
        var effectiveScale = _temporaryMiniMapScales.TryGetValue(imageKey, out var temporaryScale)
            ? temporaryScale
            : miniMapScale;
        if (!MapOverlayBitmapRenderer.TryGetScaledImageSize(
                imagePath,
                effectiveScale,
                out var scaledWidth,
                out var scaledHeight))
        {
            _persistentMiniMap = null;
            _miniMapScale = null;
            _miniMapBaseScale = null;
            _miniMapImageKey = null;
            return;
        }
        _persistentMiniMap = new MapOverlayRenderMap(
            imagePath,
            0, 0,
            scaledWidth,
            scaledHeight,
            anchors ?? (IReadOnlyList<MapOverlayRenderAnchor>)Array.Empty<MapOverlayRenderAnchor>(),
            null,
            annotations,
            floorLabel);
        _miniMapScale = effectiveScale;
        _miniMapBaseScale = miniMapScale;
        _miniMapImageKey = imageKey;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void ClearPersistentMiniMap()
    {
        _persistentMiniMap = null;
        _miniMapScale = null;
        _miniMapBaseScale = null;
        _miniMapImageKey = null;
        RefreshVisibleContent();
    }

    public void ClearTemporaryMiniMapScales()
    {
        _temporaryMiniMapScales.Clear();
        if (_miniMapBaseScale is double baseScale)
            SetMiniMapScaleCore(baseScale);
    }

    private void SetMiniMapScaleCore(double scale)
    {
        if (_persistentMiniMap is not { } miniMap
            || !MapOverlayBitmapRenderer.TryGetScaledImageSize(
                miniMap.ImagePath, scale, out var width, out var height))
        {
            return;
        }

        _persistentMiniMap = miniMap with { Width = width, Height = height };
        _miniMapScale = scale;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        InvalidateLockedBackground();
        _nativeWindow.Dispose();
        _map = null;
        _player = null;
        _status = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
/*
 * 文件职责：MapOverlayWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
