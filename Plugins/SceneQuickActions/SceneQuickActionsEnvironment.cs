using IDVBuff.PluginContracts;
using OpenCvSharp;

namespace IDVBuff.Plugins.SceneQuickActions;

internal interface ISceneQuickActionsFrameGrabber
{
    bool HasFastPath { get; }
    bool TryGrab(out GrabbedFrame? frame, out string failure, CancellationToken token);
}

internal interface ISceneQuickActionsMatcher
{
    bool TryMatch(Mat grayFrame, string templateName, double threshold, out MatchHit hit);
}

/// <summary>场景动作共用的输入和窗口边界；所有调用均由 runner 的整段操作所有权保护。</summary>
internal interface ISceneQuickActionsInput
{
    bool IsForegroundWindow(IntPtr window);
    bool TryGetForegroundGameWindow(out IntPtr window);
    bool TryGetClientBounds(IntPtr window, out PluginClientBounds bounds);
    void MoveSmoothly(int x, int y, int durationMilliseconds, CancellationToken token);
    void MoveCursorTo(int x, int y);
    void ClickLeft(int holdMilliseconds);
    void SetLeftButton(bool down);
    void InjectKey(uint virtualKey, int holdMilliseconds);
    void InjectBinding(PluginInputBinding binding, int holdMilliseconds);
}

internal sealed class SceneQuickActionsNativeInput : ISceneQuickActionsInput
{
    public static SceneQuickActionsNativeInput Instance { get; } = new();

    public bool IsForegroundWindow(IntPtr window) => NativeInput.IsForegroundWindow(window);
    public bool TryGetForegroundGameWindow(out IntPtr window) => NativeInput.TryGetForegroundGameWindow(out window);
    public bool TryGetClientBounds(IntPtr window, out PluginClientBounds bounds) => NativeInput.TryGetClientBounds(window, out bounds);
    public void MoveSmoothly(int x, int y, int durationMilliseconds, CancellationToken token) =>
        NativeInput.MoveSmoothly(x, y, durationMilliseconds, token);
    public void MoveCursorTo(int x, int y) => NativeInput.MoveCursorTo(x, y);
    public void ClickLeft(int holdMilliseconds) => NativeInput.ClickLeft(holdMilliseconds);
    public void SetLeftButton(bool down) => NativeInput.SetLeftButton(down);
    public void InjectKey(uint virtualKey, int holdMilliseconds) => NativeInput.InjectKey(virtualKey, holdMilliseconds);
    public void InjectBinding(PluginInputBinding binding, int holdMilliseconds) => NativeInput.InjectBinding(binding, holdMilliseconds);
}
