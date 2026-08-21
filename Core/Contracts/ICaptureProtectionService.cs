namespace IDVBuff.Core.Contracts;

/// <summary>窗口在系统录屏/截图中的保护类别。</summary>
public enum CaptureProtectionWindowCategory
{
    MainProgram,
    DisplayLayer
}

/// <summary>
/// 捕获保护服务登记的窗口句柄。句柄销毁或重建时必须释放旧登记。
/// </summary>
public interface ICaptureProtectionRegistration : IDisposable
{
    IntPtr Handle { get; }

    CaptureProtectionWindowCategory Category { get; }

    string Name { get; }

    /// <summary>最近一次策略应用是否成功启用了排除。</summary>
    bool IsProtectionApplied { get; }
}

/// <summary>
/// 宿主级系统捕获保护策略。实现位于主 WinUI 项目；插件和 Core 只依赖此契约。
/// </summary>
public interface ICaptureProtectionService : IDisposable
{
    bool IsPluginEnabled { get; }

    bool IsProtectionRequested(CaptureProtectionWindowCategory category);

    /// <summary>
    /// 设置总门控和两个相互独立的类别策略。总门控关闭时两类窗口均恢复 WDA_NONE。
    /// </summary>
    void SetPolicy(
        bool pluginEnabled,
        bool hideMainProgram,
        bool hideDisplayLayer);

    ICaptureProtectionRegistration RegisterWindow(
        IntPtr handle,
        CaptureProtectionWindowCategory category,
        string name);

    /// <summary>为已登记窗口重新应用当前策略。</summary>
    void RefreshPolicy();
}
