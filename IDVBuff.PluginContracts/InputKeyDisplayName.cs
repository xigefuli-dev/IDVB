namespace IDVBuff.PluginContracts;

/// <summary>
/// 将 Win32 虚拟键码转换为用户可读的按键名称。
/// 绑定的持久化值仍使用虚拟键码；此类型只负责展示，避免界面泄漏 VK/VC 代码。
/// </summary>
public static class InputKeyDisplayName
{
    public static string FormatVirtualKey(uint key)
    {
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)key).ToString();
        if (key is >= 0x60 and <= 0x69)
            return $"小键盘 {key - 0x60}";
        if (key is >= 0x70 and <= 0x87)
            return $"F{key - 0x6F}";

        return key switch
        {
            0x01 => "鼠标左键",
            0x02 => "鼠标右键",
            0x04 => "鼠标中键",
            0x05 => "鼠标侧键 1",
            0x06 => "鼠标侧键 2",
            0x08 => "退格键",
            0x09 => "Tab",
            0x0C => "Clear",
            0x0D => "Enter",
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x15 => "输入法切换",
            0x1B => "Esc",
            0x20 => "空格",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "左方向键",
            0x26 => "上方向键",
            0x27 => "右方向键",
            0x28 => "下方向键",
            0x29 => "Select",
            0x2A => "Print",
            0x2B => "Execute",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x2F => "Help",
            0x5B or 0x5C => "Windows",
            0x5D => "菜单键",
            0x6A => "小键盘 *",
            0x6B => "小键盘 +",
            0x6C => "小键盘分隔符",
            0x6D => "小键盘 -",
            0x6E => "小键盘 .",
            0x6F => "小键盘 /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xA6 => "浏览器后退",
            0xA7 => "浏览器前进",
            0xA8 => "浏览器刷新",
            0xA9 => "浏览器停止",
            0xAA => "浏览器搜索",
            0xAB => "浏览器收藏夹",
            0xAC => "浏览器主页",
            0xAD => "静音",
            0xAE => "音量减小",
            0xAF => "音量增大",
            0xB0 => "下一曲",
            0xB1 => "上一曲",
            0xB2 => "停止播放",
            0xB3 => "播放/暂停",
            0xB4 => "启动邮件",
            0xB5 => "媒体选择",
            0xB6 => "启动应用 1",
            0xB7 => "启动应用 2",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xDF => "键盘布局按键",
            0xE2 => "国际键",
            0xE5 => "输入法处理键",
            0xE7 => "输入法数据键",
            0xF6 => "Attn",
            0xF7 => "CrSel",
            0xF8 => "ExSel",
            0xF9 => "Erase EOF",
            0xFA => "Play",
            0xFB => "Zoom",
            0xFC => "系统保留按键",
            0xFD => "PA1",
            0xFE => "Clear",
            _ => "未命名按键"
        };
    }
}
