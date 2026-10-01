using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class CatalogPublicationConcurrencyTests
{
    [Fact]
    public void ScanCacheWaitUsesExistingDeadlineAndCancellation()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-cache-wait-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var service=new MapCvRecognitionService(new MapRepository(root));
            var gate=(MapCatalogResourceGate)typeof(MapCvRecognitionService).GetField("_catalogResourceGate",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            using var held=gate.Enter();
            using var frame=new CapturedGameFrame(new Mat(100,160,MatType.CV_8UC3,Scalar.Black),
                new(0,0,1920,1080),new(400,200,160,100),IntPtr.Zero);
            using(var execution=ScanExecutionContext.Enter(ScanPerformanceMode.Fast))
            {
                var result=service.RunSideEntranceScan(frame,new());
                Assert.Equal(SideEntranceFailureStage.BudgetExceeded,result.FailureStage);
                Assert.False(result.RetrievalWasRun);Assert.Equal(0,result.GateDetection.MatchTemplateCalls);
                Assert.True(execution.RemainingMilliseconds<100);
            }
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            using(var execution=ScanExecutionContext.Enter(ScanPerformanceMode.Balanced,cancel.Token))
                Assert.Equal(SideEntranceFailureStage.Canceled,service.RunSideEntranceScan(frame,new()).FailureStage);
            Assert.False(frame.Image.IsDisposed);
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    [Fact]
    public async Task ServiceRefreshCannotRetireTemplateWhileScanOwnsIt()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-service-publication-"+Guid.NewGuid().ToString("N"));
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var repository=new MapRepository(root);
            using var service=new MapCvRecognitionService(repository);
            var template=new Mat(30,30,MatType.CV_8UC1,Scalar.White);
            typeof(MapCvRecognitionService).GetField("_sideEntranceFeatureCache",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .SetValue(service,new Dictionary<(Guid,string),Mat> {[(Guid.NewGuid(),"1f")]=template});
            using var frame=new CapturedGameFrame(new Mat(100,160,MatType.CV_8UC3,new Scalar(30,25,22)),
                new(0,0,1920,1080),new(400,200,160,100),IntPtr.Zero);
            var scan=Task.Run(()=>service.RunSideEntranceScan(frame,new(),progress:p=>
            {
                if(p!=0) return;
                entered.TrySetResult();
                if(!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test scan barrier");
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var refresh=service.RefreshCacheAsync();
            Assert.False(refresh.IsCompleted);
            Assert.False(template.IsDisposed);
            release.Set();
            var result=await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.RetrievalWasRun);
            Assert.Null(result.RetrievedCandidateCount);
            Assert.Equal(SideEntranceFailureStage.GateDetection,result.FailureStage);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(template.IsDisposed);
        }
        finally {release.Set();if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    [Fact]
    public async Task MatRetirementWaitsUntilReaderHasFinished()
    {
        var gate=new MapCatalogResourceGate();
        var image=new Mat(20,20,MatType.CV_8UC1,Scalar.White);
        var reader=gate.Enter();
        var queued=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement=Task.Run(async ()=>
        {
            queued.SetResult();
            using var writer=await gate.EnterAsync();
            image.Dispose();
        });
        await queued.Task;
        Assert.False(retirement.IsCompleted);
        Assert.Equal(255,Cv2.Mean(image).Val0);
        reader.Dispose();reader.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(image.IsDisposed);
        using var after=gate.Enter();
    }

    [Fact]
    public async Task CanceledWaiterDoesNotLeakLeaseOrDisposeCurrentReader()
    {
        var gate=new MapCatalogResourceGate();
        var reader=gate.Enter();
        using var cancel=new CancellationTokenSource();
        var pending=gate.EnterAsync(cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        reader.Dispose();
        using var next=await gate.EnterAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SnapshotRevisionCannotPublishAfterCatalogHasChanged()
    {
        var root=Path.Combine(Path.GetTempPath(),"idvb-publication-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MapRepository(root);
            var snapshot=await repository.GetCatalogSnapshotAsync();
            await repository.CreateClassAsync("changed");
            var published=false;
            Assert.False(repository.TryPublishCatalogSnapshot(snapshot.Revision,()=>published=true));
            Assert.False(published);
            var current=await repository.GetCatalogSnapshotAsync();
            Assert.NotEqual(snapshot.Revision,current.Revision);
            Assert.Contains("changed",current.Classes);
            Assert.True(repository.TryPublishCatalogSnapshot(current.Revision,()=>published=true));
            Assert.True(published);
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
}
