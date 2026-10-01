using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private readonly CalibrationOperationCoordinator _calibrationCoordinator=new();
    private readonly TextBlock _calibrationStatus=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _cancelCalibration=new(){Content="取消校准",Visibility=Visibility.Collapsed};

    private void AttachCalibrationStatus()
    {
        if(_root is not null && _root.Children.Count>1 && _root.Children[1] is StackPanel content)
        {
            content.Children.Add(_calibrationStatus);content.Children.Add(_cancelCalibration);
            _cancelCalibration.Click+=(_,_)=>
            {
                _calibrationStatus.Text="校准已取消，可重新开始。";
                _cancelCalibration.Visibility=Visibility.Collapsed;
                _calibrationCoordinator.Cancel();
            };
        }
    }
    private Task CalibrateViewportAsync()=>RunCalibrationAsync(CalibrationRegionKind.MapViewport);
    private Task CalibrateFloorDisplayAsync()=>RunCalibrationAsync(CalibrationRegionKind.FloorDisplay);
    private Task CalibrateNativeMiniMapAsync()=>RunCalibrationAsync(CalibrationRegionKind.NativeMiniMap);

    private async Task RunCalibrationAsync(CalibrationRegionKind kind)
    {
        using var operation=_calibrationCoordinator.TryStart();
        if(operation is null) {_calibrationStatus.Text="校准正在进行，请先完成或取消当前校准。";return;}
        using var scanGuard=_runtime.BeginCalibrationOperation();
        var timer=Stopwatch.StartNew();var stage="Requested";var terminal="Canceled";
        void Log(string current,Exception? error=null)
        {
            _runtime.LogCollector.Append(MapLogCategory.ViewportCapture,error is null ? MapLogLevel.Info : MapLogLevel.Error,
                "校准操作 · "+current,elapsedMs:timer.Elapsed.TotalMilliseconds,details:new()
                { ["calibrationOperationId"]=operation.Id,["stage"]=current,["kind"]=kind.ToString(),
                  ["exceptionType"]=error?.GetType().Name,["hResult"]=error?.HResult });
        }
        try
        {
            Log(stage);_cancelCalibration.Visibility=Visibility.Visible;
            var title=kind switch {CalibrationRegionKind.MapViewport=>"校准游戏地图区域",
                CalibrationRegionKind.FloorDisplay=>"校准楼层显示区",_=>"校准原生小地图区域"};
            var instructions=kind switch
            {
                CalibrationRegionKind.MapViewport=>"请沿完整地图画布的外边缘框选，不要只框建筑主体或两个门。",
                CalibrationRegionKind.FloorDisplay=>"请完整框选 1F/2F 双按钮区域，保留两个按钮及其高亮背景。",
                _=>"请框选左上角整个原生小地图，保留玩家图标和完整视野锥。"
            };
            var prompt=new ContentDialog {XamlRoot=XamlRoot,Title="准备"+title,
                Content="点击开始后，请切换到第五人格。"+(kind==CalibrationRegionKind.NativeMiniMap
                    ? "保持原生小地图可见，不要打开完整地图。" : "打开完整地图。")
                    +"程序会等待游戏前台后捕获。只保存相对坐标，校准截图不会写入磁盘。",
                PrimaryButtonText="开始",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary};
            using(var registration=operation.Token.Register(()=>DispatcherQueue.TryEnqueue(()=>prompt.Hide())))
            {
                if(await prompt.ShowAsync()!=ContentDialogResult.Primary) return;
            }
            operation.EnsureCurrent();stage="WaitingForGame";
            _calibrationStatus.Text="请切换到游戏，等待捕获画面……";
            var foregroundTimer=Stopwatch.StartNew();
            while(!_runtime.IsGameForegroundForCalibration())
            {
                operation.EnsureCurrent();
                if(foregroundTimer.Elapsed>TimeSpan.FromSeconds(10)) throw new TimeoutException("未等到游戏前台，请重试。");
                await Task.Delay(250,operation.Token);
            }
            stage="Capture";
            using var snapshot=await _runtime.CaptureCalibrationSnapshotAsync(operation.Token);
            operation.EnsureCurrent();Log("CaptureSucceeded");
            stage="HostReady";
            var app=(App)Application.Current;app.RestoreMainWindowForCalibration();
            var hostTimer=Stopwatch.StartNew();
            while(XamlRoot is null || XamlRoot.Size.Width<320 || XamlRoot.Size.Height<320)
            {
                operation.EnsureCurrent();
                if(hostTimer.Elapsed>TimeSpan.FromSeconds(2)) throw new TimeoutException("校准宿主尚未恢复可见，请重试。");
                await Task.Delay(50,operation.Token);
            }
            if(!IsLoaded || XamlRoot is null || app.MainWindow.Content is not FrameworkElement host
                || !ReferenceEquals(host.XamlRoot,XamlRoot)) throw new InvalidOperationException("校准宿主已变化，请返回地图页面重试。");
            Log(stage);
            var width=snapshot.Window.ClientWidth;var height=snapshot.Window.ClientHeight;
            var current=kind switch
            {
                CalibrationRegionKind.MapViewport=>_runtime.ResolveEffectiveViewport(width,height).Region,
                CalibrationRegionKind.FloorDisplay=>_runtime.Settings.ResolveFloorDisplayRegion(width,height),
                _=>_runtime.Settings.ResolveNativeMiniMapRegion(width,height)
            };
            stage="Dialog";_calibrationStatus.Text="请框选区域并保存，或取消此次校准。";
            var region=await MapViewportCalibrationDialog.ShowAsync(XamlRoot,snapshot.Frame,current,title,
                instructions+"只保存相对坐标，截图不会写入磁盘。",operation.Token,()=>Log("DialogOpened"));
            operation.EnsureCurrent();if(region is null) return;
            if(!snapshot.Window.Matches(DwrGameWindowCaptureService.GetCalibrationWindowIdentity(snapshot.Window.WindowHandle)))
                throw new InvalidOperationException("游戏窗口、客户区大小或 DPI 已改变，请重新捕获。");
            stage="SaveStarted";Log(stage);
            var result=await _runtime.SaveCalibrationRegionAsync(kind,region,snapshot,operation.Token);
            operation.EnsureCurrent();
            if(!result.Succeeded) throw new InvalidOperationException(result.Failure);
            stage="SaveVerified";Log(stage);terminal="Completed";
            _calibrationStatus.Text=title+"已保存并验证生效。";
            Refresh();
        }
        catch(OperationCanceledException) {terminal="Canceled";}
        catch(Exception error)
        {
            terminal="Failed";Log(stage,error);
            if(operation.IsCurrent) _calibrationStatus.Text="校准未完成："+error.Message+" 可重新开始。";
        }
        finally
        {
            if(operation.TryFinish()) Log(terminal);
            if(operation.IsCurrent)
            {
                _cancelCalibration.Visibility=Visibility.Collapsed;
                if(terminal=="Canceled") _calibrationStatus.Text="校准已取消，可重新开始。";
            }
        }
    }
}
