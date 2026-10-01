using System.Globalization;
using IDVBuff.Core.Models;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps.Adapters;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class CalibrationRecoveryTests
{
    [Fact]
    public void CalibrationRejectsQueuedEdgesAndRequiresNewInputAfterReentry()
    {
        var gate = new CalibrationInputGate();
        Assert.False(gate.Reject());
        using var first = gate.Begin();
        var heldEdge = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(gate.Reject(heldEdge));
        using var second = gate.Begin();
        first.Dispose(); first.Dispose();
        Assert.True(gate.Reject());
        second.Dispose();
        Assert.False(gate.IsActive);
        Assert.True(gate.Reject(heldEdge));
        Assert.False(gate.Reject(long.MaxValue));
    }

    [Fact]
    public void CancelReenterAndLateCompletionCannotRevokeNewOperation()
    {
        var coordinator = new CalibrationOperationCoordinator();
        using var old = coordinator.TryStart()!;
        Assert.Null(coordinator.TryStart());
        coordinator.Cancel();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(old.EnsureCurrent);
        using var current = coordinator.TryStart()!;
        old.Dispose();
        Assert.True(current.IsCurrent);
        Assert.NotEqual(old.Id, current.Id);
        Assert.True(old.TryFinish());
        Assert.False(old.TryFinish());
        current.Dispose();
        using var next = coordinator.TryStart();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task CancellationDisposesLateNativeCaptureWithoutBlockingReentry()
    {
        var capture = new TaskCompletionSource<OwnedMat>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var wait = CalibrationOperationCoordinator.AwaitOwnedResultAsync(capture.Task, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        var owned = new OwnedMat();
        capture.SetResult(owned);
        await owned.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(owned.Image.IsDisposed);
    }

    [Fact]
    public async Task CaptureFailureRemainsObservableAndNextOperationCanStart()
    {
        var coordinator = new CalibrationOperationCoordinator();
        using (var operation = coordinator.TryStart()!)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CalibrationOperationCoordinator.AwaitOwnedResultAsync(
                    Task.FromException<OwnedMat>(new InvalidOperationException("capture failed")), operation.Token));
        using var next = coordinator.TryStart();
        Assert.NotNull(next);
    }

    [Fact]
    public void EffectiveRegionUsesMatchingPresetAndRejectsInvalidOrOtherGeometry()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var saved = new NormalizedRectangle { X=.2, Y=.1, Width=.5, Height=.7 };
        settings.UpsertMapViewportCalibration(saved,1920,1080,96);
        var toml = new ViewportCalibrationConfig { ClientWidth=1920, ClientHeight=1080,
            MapRegionX=.1, MapRegionY=.2, MapRegionWidth=.7, MapRegionHeight=.6 };
        var preset = EffectiveViewportResolver.Resolve(settings,toml,1920,1080,"1920",7);
        Assert.Equal(EffectiveViewportSource.Preset,preset.Source);
        Assert.Equal(.1,preset.Region.X);
        Assert.Equal(7,preset.Revision);
        toml.MapRegionX=double.NaN;
        Assert.Equal(EffectiveViewportSource.Settings,
            EffectiveViewportResolver.Resolve(settings,toml,1920,1080,"1920").Source);
        Assert.Equal(EffectiveViewportSource.FullClient,
            EffectiveViewportResolver.Resolve(settings,toml,1600,900,"1920").Source);
        Assert.False(EffectiveViewportResolver.IsValid(new() { X=.9, Y=0, Width=.2, Height=1 }));
        var shifted=saved.Clone();shifted.X+=.001;
        Assert.False(EffectiveViewportResolver.MatchesPixels(saved,shifted,1920,1080));
        Assert.True(EffectiveViewportResolver.MatchesPixels(saved,saved.Clone(),1920,1080));
    }

    [Fact]
    public async Task SaveReadbackAndAtomicTomlRemainInvariantUnderCommaCulture()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-calibration-"+Guid.NewGuid().ToString("N"));
        var prior=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var region=new NormalizedRectangle { X=.123456789, Y=.2, Width=.6, Height=.7 };
            var repository=new MapRuntimeSettingsRepository(root);
            var settings=MapRuntimeSettings.CreateDefault();
            await repository.SaveAsync(settings);
            var original=await File.ReadAllTextAsync(Path.Combine(root,"settings.json"));
            settings.UpsertMapViewportCalibration(region,1920,1080,144);
            await repository.SaveAsync(settings,preservePrevious:true);
            Assert.Equal(original,await File.ReadAllTextAsync(Path.Combine(root,"settings.json.calibration.bak")));
            var readback=(await repository.LoadAsync()).ResolveMapViewportRegion(1920,1080)!;
            Assert.True(EffectiveViewportResolver.MatchesPixels(region,readback,1920,1080));
            await ViewportCalibrationTomlWriter.WriteAsync(root,region,1920,1080);
            var first=await File.ReadAllTextAsync(Path.Combine(root,"viewport.toml"));
            Assert.Contains("map_region_x = 0.123456789",first);
            await ViewportCalibrationTomlWriter.WriteAsync(root,region,1920,1080);
            Assert.Equal(first,await File.ReadAllTextAsync(Path.Combine(root,"viewport.toml.calibration.bak")));
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
                ViewportCalibrationTomlWriter.WriteAsync(root,region,1920,1080,canceled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>repository.SaveAsync(settings,canceled.Token));
            Assert.Equal(first,await File.ReadAllTextAsync(Path.Combine(root,"viewport.toml")));
            Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        }
        finally
        {
            CultureInfo.CurrentCulture=prior;
            if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Fact]
    public void ChangedWindowProcessSizeAndDpiRequireRecapture()
    {
        var frozen=new CalibrationWindowIdentity((IntPtr)12,34,1920,1080,144);
        Assert.True(frozen.Matches(frozen with {}));
        Assert.False(frozen.Matches(frozen with { WindowHandle=(IntPtr)13 }));
        Assert.False(frozen.Matches(frozen with { ProcessId=35 }));
        Assert.False(frozen.Matches(frozen with { ClientWidth=1919 }));
        Assert.False(frozen.Matches(frozen with { Dpi=96 }));
    }

    [Fact]
    public async Task FailedSavePreservesPreviousFileAndAllowsRetry()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-calibration-failure-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MapRuntimeSettingsRepository(root);
            var settings=MapRuntimeSettings.CreateDefault();await repository.SaveAsync(settings);
            var path=Path.Combine(root,"settings.json");var before=File.ReadAllBytes(path);
            settings.UpsertMapViewportCalibration(new() {X=.2,Y=.1,Width=.6,Height=.8},1920,1080,96);
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var error=await Record.ExceptionAsync(()=>repository.SaveAsync(settings,preservePrevious:true));
                Assert.True(error is IOException or UnauthorizedAccessException,error?.GetType().Name);
            }
            Assert.Equal(before,File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(root,"*.tmp"));
            await repository.SaveAsync(settings,preservePrevious:true).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull((await repository.LoadAsync()).ResolveMapViewportRegion(1920,1080));
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    private sealed class OwnedMat : IDisposable
    {
        public Mat Image {get;}=new(8,8,MatType.CV_8UC1,Scalar.White);
        public TaskCompletionSource Disposed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() {Image.Dispose();Disposed.TrySetResult();}
    }

    [Fact]
    public async Task RuntimeContractForwardsCancellationBackupAndTypedReadback()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-calibration-adapter-"+Guid.NewGuid().ToString("N"));
        try
        {
            ISettingsRepository adapter=new SettingsRepositoryAdapter(new MapRuntimeSettingsRepository(root));
            var settings=MapRuntimeSettings.CreateDefault();await adapter.SaveAsync(settings);
            var before=File.ReadAllBytes(Path.Combine(root,"settings.json"));
            settings.UpsertMapViewportCalibration(new() {X=.2,Y=.1,Width=.6,Height=.8},1920,1080,96);
            await adapter.SaveAsync(settings,CancellationToken.None,true);
            Assert.Equal(before,File.ReadAllBytes(Path.Combine(root,"settings.json.calibration.bak")));
            var stored=Assert.IsType<MapRuntimeSettings>(await adapter.LoadAsync());
            Assert.NotNull(stored.ResolveMapViewportRegion(1920,1080));
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>adapter.SaveAsync(settings,canceled.Token,true));
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
}
