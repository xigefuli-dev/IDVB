using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private async Task CalibrateNativeMiniMapAsync()
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "准备校准原生小地图区域",
            Content = "点击开始后，请在 3 秒内切换到第五人格，保持左上角原生小地图可见，不要打开完整地图。"
                + "随后框选整个原生小地图，包含玩家图标和视野锥。开启诊断模式并开始对局后，每 5 秒保存一张区域截图。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary) return;
        _status.Text = "请切换到游戏，3 秒后捕获原生小地图……";
        await Task.Delay(3000);
        if (!_runtime.TryCaptureCalibrationFrame(out var frame, out var reason) || frame is null)
        {
            _status.Text = reason;
            return;
        }
        using (frame)
        {
            ((App)Application.Current).MainWindow.Activate();
            var width = (int)Math.Round(frame.ClientBounds.Width);
            var height = (int)Math.Round(frame.ClientBounds.Height);
            var region = await MapViewportCalibrationDialog.ShowAsync(
                XamlRoot, frame, _runtime.Settings.ResolveNativeMiniMapRegion(width, height),
                "校准原生小地图区域", "请框选左上角整个原生小地图，保留玩家图标和完整视野锥。此操作只保存区域坐标。");
            if (region is null) return;
            try
            {
                await _runtime.SetNativeMiniMapRegionAsync(region, width, height,
                    DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle));
            }
            catch (Exception exception)
            {
                _status.Text = $"原生小地图校准保存失败：{exception.Message}";
                return;
            }
        }
        _status.Text = "原生小地图区域已保存。开启诊断模式并开始对局后，游戏位于前台时每 5 秒保存截图；开启日志收集可查看 native_minimap 记录。";
    }
}
