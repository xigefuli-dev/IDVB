namespace IDVBuff.Core.Contracts;

/// <summary>
/// 游戏内浮层提示（跟随游戏窗口居中）。给插件与后台管线一个发布简短提示的通道，
/// 走的是宿主统一的通知中心——本体「对局状态开关」用的就是同一条路径。
///
/// 提示是游戏叠加层上的短暂文字，不是 Windows 弹窗，不会抢焦点。
/// </summary>
public interface IGameOverlayToast
{
    /// <summary>一般通知（绿色系）。</summary>
    void Notice(string message);

    /// <summary>警告（橙色系）。</summary>
    void Warning(string message);

    /// <summary>错误（红色系）。</summary>
    void Error(string message);
}
