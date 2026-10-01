using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly SemaphoreSlim _viewportConfigurationGate=new(1,1);
    private long _viewportConfigurationRevision;
    private readonly CalibrationInputGate _calibrationInput = new();

    public IDisposable BeginCalibrationOperation()
    {
        var guard = _calibrationInput.Begin();
        CancelQuickScan();
        CancelMapObservation();
        CancelMapOpenAlignment();
        return guard;
    }
    public bool IsGameForegroundForCalibration() =>
        !_disposed && _captureSvc.TryGetForegroundClientBounds(out _,out _,out _);

    public async Task<CalibrationCaptureSnapshot> CaptureCalibrationSnapshotAsync(CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var configurationRevision=Interlocked.Read(ref _viewportConfigurationRevision);
        var preset=_config.ActiveResolutionPreset;
        var work=Task.Run(() =>
        {
            if(!_captureSvc.TryGetForegroundClientBounds(out _,out var handle,out var reason))
                throw new InvalidOperationException(reason);
            var identity=DwrGameWindowCaptureService.GetCalibrationWindowIdentity(handle)
                ?? throw new InvalidOperationException("无法冻结游戏窗口身份，请重试。");
            if(!TryCaptureCalibrationFrame(out var frame,out reason) || frame is null)
                throw new InvalidOperationException(reason);
            try
            {
            if(frame.Image.Empty() || frame.WindowHandle!=identity.WindowHandle
                || frame.Image.Width!=identity.ClientWidth || frame.Image.Height!=identity.ClientHeight
                || !identity.Matches(DwrGameWindowCaptureService.GetCalibrationWindowIdentity(handle)))
            {
                throw new InvalidOperationException("捕获期间游戏窗口发生变化，请重新校准。");
            }
            return new CalibrationCaptureSnapshot(frame,identity,DateTimeOffset.UtcNow,configurationRevision,preset);
            }
            catch {frame.Dispose();throw;}
        });
        try {return await CalibrationOperationCoordinator.AwaitOwnedResultAsync(work,timeout.Token);}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {throw new TimeoutException("游戏截图超时，请重试。迟到截图会自动释放。");}
    }

    public EffectiveViewport ResolveEffectiveViewport(int width,int height) =>
        EffectiveViewportResolver.Resolve(Settings,_config.Get<ViewportCalibrationConfig>("viewport"),
            width,height,_config.ActiveResolutionPreset,Interlocked.Read(ref _viewportConfigurationRevision));

    public async Task<ViewportCalibrationSaveResult> SaveCalibrationRegionAsync(CalibrationRegionKind kind,
        NormalizedRectangle region,CalibrationCaptureSnapshot snapshot,CancellationToken cancellationToken)
    {
        var width=snapshot.Window.ClientWidth;var height=snapshot.Window.ClientHeight;var dpi=snapshot.Window.Dpi;
        if(kind==CalibrationRegionKind.MapViewport) return await SaveVerifiedViewportAsync(region,width,height,dpi,
            cancellationToken,snapshot.ConfigurationRevision,snapshot.Preset);
        if(!EffectiveViewportResolver.IsValid(region)) throw new ArgumentException("校准区域必须位于客户区内。");
        await _viewportConfigurationGate.WaitAsync(cancellationToken);
        try
        {
            ValidateCalibrationConfiguration(snapshot.ConfigurationRevision,snapshot.Preset);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate=Settings.Clone();
            if(kind==CalibrationRegionKind.FloorDisplay) candidate.UpsertFloorDisplayCalibration(region,width,height,dpi);
            else candidate.UpsertNativeMiniMapCalibration(region,width,height,dpi);
            await _settingsRepo.SaveAsync(candidate,cancellationToken,preservePrevious:true);
            if(kind==CalibrationRegionKind.FloorDisplay) Settings.UpsertFloorDisplayCalibration(region,width,height,dpi);
            else Settings.UpsertNativeMiniMapCalibration(region,width,height,dpi);
            var stored=await _settingsRepo.LoadAsync() as MapRuntimeSettings
                ?? throw new InvalidDataException("校准回读返回了无效设置。");
            var actual=kind==CalibrationRegionKind.FloorDisplay ? stored.ResolveFloorDisplayRegion(width,height)
                : stored.ResolveNativeMiniMapRegion(width,height);
            var verified=actual is not null && EffectiveViewportResolver.MatchesPixels(region,actual,width,height);
            return new(true,true,true,verified,null,verified ? null : "保存回读不一致，请重试。");
        }
        finally {_viewportConfigurationGate.Release();}
    }

    private async Task<ViewportCalibrationSaveResult> SaveVerifiedViewportAsync(NormalizedRectangle region,
        int width,int height,uint dpi,CancellationToken cancellationToken,long? expectedRevision=null,string? expectedPreset=null)
    {
        if(!EffectiveViewportResolver.IsValid(region) || width<=0 || height<=0)
            throw new ArgumentException("地图区域必须位于客户区内，且坐标和面积有效。");
        await _viewportConfigurationGate.WaitAsync(cancellationToken);
        var persisted=false;var preset=false;var reloaded=false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(expectedRevision is { } frozenRevision) ValidateCalibrationConfiguration(frozenRevision,expectedPreset!);
            var candidate=Settings.Clone();
            candidate.UpsertMapViewportCalibration(region,width,height,dpi);
            await _settingsRepo.SaveAsync(candidate,cancellationToken,preservePrevious:true);
            persisted=true;
            Settings.UpsertMapViewportCalibration(region,width,height,dpi);
            cancellationToken.ThrowIfCancellationRequested();
            preset=await WriteViewportCalibrationToPresetAsync(width,height,dpi,cancellationToken);
            if(!preset) throw new InvalidOperationException("没有可写入的匹配预设，JSON 已保存，地图区域尚未验证生效。");
            _config.Reload();reloaded=true;
            Interlocked.Increment(ref _viewportConfigurationRevision);
            var stored=await _settingsRepo.LoadAsync() as MapRuntimeSettings
                ?? throw new InvalidDataException("校准回读返回了无效设置。");
            var effective=EffectiveViewportResolver.Resolve(stored,_config.Get<ViewportCalibrationConfig>("viewport"),
                width,height,_config.ActiveResolutionPreset,_viewportConfigurationRevision);
            var verified=EffectiveViewportResolver.MatchesPixels(region,effective.Region,width,height);
            return new(persisted,preset,reloaded,verified,effective,
                verified ? null : "校准已保存，但当前实际地图区域与提交区域不一致，请重新校准。");
        }
        catch(Exception error)
        {
            _logCollector.Append(MapLogCategory.System,MapLogLevel.Error,"地图区域校准尚未完成",details:new()
            { ["settingsPersisted"]=persisted,["presetPersisted"]=preset,["reloaded"]=reloaded,
              ["exceptionType"]=error.GetType().Name,["hResult"]=error.HResult });
            return new(persisted,preset,reloaded,false,ResolveEffectiveViewport(width,height),
                persisted ? "校准部分保存，但尚未验证生效。请重试；原 JSON 和预设备份仍保留。" : "校准保存失败，未确认持久化成功。请重试。");
        }
        finally {_viewportConfigurationGate.Release();}
    }

    private void ValidateCalibrationConfiguration(long revision,string preset)
    {
        if(revision!=Interlocked.Read(ref _viewportConfigurationRevision)
            || !string.Equals(preset,_config.ActiveResolutionPreset,StringComparison.Ordinal))
            throw new InvalidOperationException("校准期间分辨率预设已改变，请重新捕获。");
    }
}
