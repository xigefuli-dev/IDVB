using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed class GateOcclusionControlsTests(ITestOutputHelper output)
{
    private static string GatePath=>Path.Combine(AppContext.BaseDirectory,"Assets","Gate.png");

    [Theory]
    [InlineData(1600,36,.32,39,67)]
    [InlineData(1920,46,.43,157,88)]
    [InlineData(2560,56,.38,83,137)]
    public void SyntheticDifferentScalesLocationsAndPlayerCoverageAreConfirmed(
        int clientWidth,int width,double coverage,int x,int y)
    {
        using var detector=new GateTemplateDetector(GatePath);
        using var canvas=CreateCanvas();using var gate=ResizeGate(width);
        Paste(gate,canvas,x,y);
        Cv2.Rectangle(canvas,new Rect(x,y,(int)Math.Round(width*coverage),gate.Height),new Scalar(170,100,45),-1);
        SaveControl(canvas,$"positive-{clientWidth}-{width}");
        var result=detector.DetectPlayerOccludedGates(canvas,new(0,0,canvas.Width,canvas.Height),clientWidth,new());
        output.WriteLine($"synthetic width={width}, coverage={coverage}: {result.Gates.Count}; {result.ElapsedMilliseconds:F1}ms");
        var found=Assert.Single(result.Gates);
        Assert.InRange(Math.Abs(found.ScreenBounds.X-x),0,2);
        Assert.InRange(Math.Abs(found.ScreenBounds.Y-y),0,2);
        Assert.Equal(GateEvidenceKind.PlayerOcclusionConfirmed,found.EvidenceKind);
        Assert.True(found.WholeTemplateScore<found.Score);
    }

    [Fact]
    public void SyntheticCompleteGateStillUsesFullTemplatePath()
    {
        using var detector=new GateTemplateDetector(GatePath);
        using var canvas=CreateCanvas();using var gate=ResizeGate(46);Paste(gate,canvas,77,111);
        SaveControl(canvas,"positive-complete-gate");
        using var gray=GateTemplateDetector.CreateMatchImage(canvas);
        var result=detector.Detect(gray,new(0,0,canvas.Width,canvas.Height),1920,.72,
            new() {Mode=GateSearchMode.LockedScale,LockedScale=.46});
        var found=Assert.Single(result.Gates);
        Assert.Equal(GateEvidenceKind.FullTemplate,found.EvidenceKind);
        Assert.True(found.Score>.99);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wall-player")]
    [InlineData("chest-player")]
    [InlineData("text-player")]
    [InlineData("arrow-only")]
    [InlineData("vertical-frame-only")]
    [InlineData("inconsistent-pieces")]
    [InlineData("no-player")]
    [InlineData("right-occlusion")]
    public void SyntheticNegativeControlsCannotBecomeConfirmedGates(string control)
    {
        using var detector=new GateTemplateDetector(GatePath);
        using var canvas=CreateCanvas();using var gate=ResizeGate(46);
        const int x=111,y=123;
        if(control is "no-player" or "right-occlusion")
        {
            Paste(gate,canvas,x,y);
            if(control=="right-occlusion") Cv2.Rectangle(canvas,new Rect(x+23,y,23,gate.Height),new Scalar(170,100,45),-1);
        }
        else if(control!="empty")
        {
            Cv2.Circle(canvas,new(x+9,y+23),18,new Scalar(170,100,45),-1);
            if(control=="wall-player") Cv2.Line(canvas,new(x-40,y+22),new(x+85,y+22),new Scalar(30,140,240),3);
            if(control=="chest-player") Cv2.Rectangle(canvas,new Rect(x+17,y+15,29,24),new Scalar(35,140,240),2);
            if(control=="text-player") Cv2.PutText(canvas,"2F EXIT",new(x+17,y+24),HersheyFonts.HersheySimplex,.6,Scalar.White,2);
            if(control is "arrow-only" or "inconsistent-pieces")
            {
                var arrow=new Rect(24,17,19,13);
                using var support=new Mat(gate,arrow);Paste(support,canvas,x+arrow.X,y+arrow.Y);
            }
            if(control=="vertical-frame-only") Cv2.Line(canvas,new(x+35,y+7),new(x+35,y+39),Scalar.White,3);
            if(control=="inconsistent-pieces")
            {
                using var upper=new Mat(gate,new Rect(24,7,12,9));Paste(upper,canvas,x+27,y+7);
                using var lower=new Mat(gate,new Rect(24,31,12,7));Paste(lower,canvas,x+19,y+34);
            }
        }
        var result=detector.DetectPlayerOccludedGates(canvas,new(0,0,canvas.Width,canvas.Height),1920,new());
        SaveControl(canvas,"negative-"+control);
        output.WriteLine($"synthetic {control}: {result.Gates.Count} gates, {result.OcclusionEvidence.Count} proposals");
        Assert.Empty(result.Gates);
    }

    [Fact]
    public void CancellationAndExpiredBudgetRejectPartialProposals()
    {
        using var detector=new GateTemplateDetector(GatePath);
        using var canvas=CreateCanvas();using var gate=ResizeGate(46);Paste(gate,canvas,77,111);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        var canceled=detector.DetectPlayerOccludedGates(canvas,new(0,0,canvas.Width,canvas.Height),1920,
            new() {CancellationToken=cancel.Token});
        Assert.Empty(canceled.Gates);Assert.Equal(GateSearchStopReason.Canceled,canceled.StopReason);
        Assert.False(canceled.BudgetExceeded);
        var expired=detector.DetectPlayerOccludedGates(canvas,new(0,0,canvas.Width,canvas.Height),1920,
            new() {TimeBudgetMilliseconds=0});
        Assert.Empty(expired.Gates);Assert.Equal(0,expired.MatchTemplateCalls);
        Assert.True(expired.BudgetExceeded);
    }

    [Fact]
    public void UnknownGateAssetUsesStrictPathOnly()
    {
        var path=Path.Combine(Path.GetTempPath(),"idvb-unknown-gate-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            using var gate=ResizeGate(46);Cv2.ImWrite(path,gate);
            using var detector=new GateTemplateDetector(path);using var canvas=CreateCanvas();
            Assert.Equal(GateSearchStopReason.InvalidSearchContext,
                detector.DetectPlayerOccludedGates(canvas,new(0,0,canvas.Width,canvas.Height),1920,new()).StopReason);
        }
        finally {File.Delete(path);}
    }

    private static Mat CreateCanvas()=>new(280,360,MatType.CV_8UC3,new Scalar(40,45,50));
    private static void SaveControl(Mat canvas,string name)
    {
        var root=Environment.GetEnvironmentVariable("IDVB_REPLAY_OUTPUT_DIR");
        if(root is null) return;
        var directory=Path.Combine(root,"synthetic-controls");Directory.CreateDirectory(directory);
        Assert.True(Cv2.ImWrite(Path.Combine(directory,name+".png"),canvas));
    }
    private static Mat ResizeGate(int width)
    {
        using var source=Cv2.ImRead(GatePath);var scaled=new Mat();
        Cv2.Resize(source,scaled,new(width,(int)Math.Round(width*101d/100)),interpolation:InterpolationFlags.Area);
        return scaled;
    }
    private static void Paste(Mat source,Mat target,int x,int y)
    {using var destination=new Mat(target,new Rect(x,y,source.Width,source.Height));source.CopyTo(destination);}
}
