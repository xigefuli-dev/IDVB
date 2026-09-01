using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private readonly ToggleSwitch _diagnosticModeToggle = new()
    {
        Header = "诊断模式",
        OffContent = "已关闭",
        OnContent = "按对局收集配准图"
    };
}
