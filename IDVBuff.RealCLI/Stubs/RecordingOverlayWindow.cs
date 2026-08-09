// IDVB Real CLI — 记录型叠加窗口
// 实现 IOverlayWindow，记录所有状态变更但不执行任何渲染操作。
// 暴露 LastRecognition 等属性供 CLI 收集识别结果。

using IDVBuff.Core.Contracts;

namespace IDVBuff.RealCLI.Stubs;

/// <summary>
/// 记录型叠加窗口。Save 所有 Update/Show/Hide/Clear 调用及参数，
/// 不执行实际的 Win32 窗口创建和 GDI+ 渲染。
/// </summary>
public sealed class RecordingOverlayWindow : IOverlayWindow
{
    private readonly List<string> _events = new();

    public bool IsVisible { get; private set; }
    public bool HasMap { get; private set; }

    /// <summary>叠加窗口操作事件日志。</summary>
    public IReadOnlyList<string> Events => _events;

    /// <summary>最后一次 UpdateMap 传入的 RuntimeMapRecognition。</summary>
    public object? LastRecognition { get; private set; }

    /// <summary>最后一次 UpdateMap/UpdateStatus 传入的游戏窗口区域。</summary>
    public object? LastGameBounds { get; private set; }

    /// <summary>最后一次 UpdateStatus 传入的状态对象。</summary>
    public object? LastStatus { get; private set; }

    /// <summary>最后一次 SetPersistentMiniMapState 传入的图片路径。</summary>
    public string? LastMiniMapImagePath { get; private set; }

    /// <summary>最后一次 SetPersistentMiniMapState 传入的楼层标签。</summary>
    public string? LastMiniMapFloorLabel { get; private set; }

    public void UpdateMap(
        object recognition,
        object gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        object? viewportBounds = null,
        bool preservePlayer = false)
    {
        LastRecognition = recognition;
        LastGameBounds = gameBounds;
        HasMap = true;
        _events.Add($"UpdateMap(hwnd=0x{gameWindowHandle.ToInt64():X}, showStatus={showStatusPreference})");
        Show();
    }

    public void UpdateStatus(
        object status,
        object gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool showImmediately = true)
    {
        LastStatus = status;
        LastGameBounds = gameBounds;
        _events.Add($"UpdateStatus(showImmediately={showImmediately}, hwnd=0x{gameWindowHandle.ToInt64():X})");
    }

    public void ClearStatus()
    {
        LastStatus = null;
        _events.Add("ClearStatus");
    }

    public void UpdatePlayer(object? player)
        => _events.Add($"UpdatePlayer(player={(player is not null ? "present" : "null")})");

    public void Show()
    {
        IsVisible = true;
        _events.Add("Show");
    }

    public void Hide()
    {
        IsVisible = false;
        _events.Add("Hide");
    }

    public void Toggle()
    {
        IsVisible = !IsVisible;
        _events.Add($"Toggle -> IsVisible={IsVisible}");
    }

    public void Clear()
    {
        HasMap = false;
        LastRecognition = null;
        _events.Add("Clear");
    }

    public void ClearMap()
    {
        HasMap = false;
        LastRecognition = null;
        _events.Add("ClearMap");
    }

    public void ClearSession()
        => _events.Add("ClearSession");

    public void LockBackground(
        object recognition,
        object viewportBounds,
        object gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool preservePlayer = false)
        => _events.Add($"LockBackground(hwnd=0x{gameWindowHandle.ToInt64():X})");

    public void SetPersistentMiniMapState(
        string imagePath,
        object transform,
        object gameBounds,
        IntPtr gameWindowHandle,
        double miniMapScale,
        object? anchors = null,
        object? annotations = null,
        string? floorLabel = null)
    {
        LastMiniMapImagePath = imagePath;
        LastMiniMapFloorLabel = floorLabel;
        HasMap = true;
        _events.Add($"SetPersistentMiniMapState(path={imagePath}, scale={miniMapScale:F3}, "
            + $"floor={floorLabel ?? "<none>"})");
    }

    public void ClearPersistentMiniMap()
    {
        LastMiniMapImagePath = null;
        LastMiniMapFloorLabel = null;
        _events.Add("ClearPersistentMiniMap");
    }

    // ── 显示设置（No-op — CLI 不执行渲染）──

    public void SetStatusVisible(bool visible) => _events.Add($"SetStatusVisible({visible})");
    public void SetReverseAlternateDisplay(bool enabled) => _events.Add($"SetReverseAlternateDisplay({enabled})");
    public void SetAllowExtend(bool allow) => _events.Add($"SetAllowExtend({allow})");
    public void SetShowGateMarkers(bool show) => _events.Add($"SetShowGateMarkers({show})");
    public void SetShowAuxiliaryAnchors(bool show) => _events.Add($"SetShowAuxiliaryAnchors({show})");
    public void SetShowTextAnnotations(bool show) => _events.Add($"SetShowTextAnnotations({show})");
    public void SetShowBoxAnnotations(bool show) => _events.Add($"SetShowBoxAnnotations({show})");
    public void SetShowGateMarkersOnMiniMap(bool show) => _events.Add($"SetShowGateMarkersOnMiniMap({show})");
    public void SetShowAuxiliaryAnchorsOnMiniMap(bool show) => _events.Add($"SetShowAuxiliaryAnchorsOnMiniMap({show})");
    public void SetShowTextAnnotationsOnMiniMap(bool show) => _events.Add($"SetShowTextAnnotationsOnMiniMap({show})");
    public void SetShowBoxAnnotationsOnMiniMap(bool show) => _events.Add($"SetShowBoxAnnotationsOnMiniMap({show})");
    public void SetShowFloorOnMiniMap(bool show) => _events.Add($"SetShowFloorOnMiniMap({show})");
    public void SetStatusOpacity(double opacity) => _events.Add($"SetStatusOpacity({opacity:F2})");
    public void SetStatusOffsetX(double offsetX) => _events.Add($"SetStatusOffsetX({offsetX:F1})");
    public void SetStatusOffsetY(double offsetY) => _events.Add($"SetStatusOffsetY({offsetY:F1})");
    public void SetMiniMapOpacity(double opacity) => _events.Add($"SetMiniMapOpacity({opacity:F2})");
    public void SetMiniMapOffsetX(double offsetX) => _events.Add($"SetMiniMapOffsetX({offsetX:F1})");
    public void SetMiniMapOffsetY(double offsetY) => _events.Add($"SetMiniMapOffsetY({offsetY:F1})");
    public void SetMiniMapScale(double scale) => _events.Add($"SetMiniMapScale({scale:F3})");

    public void Dispose() { }
}
