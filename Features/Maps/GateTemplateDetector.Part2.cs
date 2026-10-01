using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed record GateOcclusionEvidence(MapScreenRect Bounds, double ArrowScore,
    double UpperFrameScore, double LowerFrameScore, double WholeScore,
    double BlueOccluderFraction, bool Confirmed, string Reason);

public sealed partial class GateTemplateDetector
{
    // The support layout is measured for this glyph. Unknown assets keep the strict path.
    private const string OcclusionAssetSha = "caf6964009e0fa967e3ac42c8d68efc174872ccf978075fb29272b09af072f0b";

    public GateDetectionResult DetectPlayerOccludedGates(Mat colorImage, MapScreenRect viewport,
        double clientWidth, GateSearchContext context)
    {
        lock (_detectionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Asset.Sha256 != OcclusionAssetSha || colorImage.Empty()
                || colorImage.Channels() is not (3 or 4) || !double.IsFinite(clientWidth) || clientWidth <= 0
                || !viewport.IsValid || !double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y)
                || viewport.Width != colorImage.Width || viewport.Height != colorImage.Height)
                return new() { Asset = Asset, StopReason = GateSearchStopReason.InvalidSearchContext };
            var timer = Stopwatch.StartNew();
            var budget = Math.Min(160, context.TimeBudgetMilliseconds ?? 160);
            bool CanSearch() => !context.CancellationToken.IsCancellationRequested
                && ScanExecutionContext.Current is not { CanCompute: false }
                && timer.Elapsed.TotalMilliseconds < budget;
            using var gray = CreateMatchImage(colorImage);
            var seeds = new List<(Point Location, int Width, double Score)>();
            var calls = 0;
            var minimumWidth = Math.Max(32, (int)Math.Round(clientWidth * .018));
            var maximumWidth = Math.Max(minimumWidth, (int)Math.Round(clientWidth * .031));
            // Four coarse sizes, then at most nine integer sizes around the best proposals.
            // Small wall/icon matches can never propose a gate here.
            var widths = new[] { 1.65, 2, 2.4, 2.8 }.Select(f => (int)Math.Round(
                Asset.Width * GateTemplateRules.ReferenceScale * clientWidth / GateTemplateRules.ReferenceClientWidth * f))
                .Where(w => w >= minimumWidth && w <= maximumWidth).Distinct();
            foreach (var width in widths)
            {
                if (!CanSearch()) break;
                var height = (int)Math.Round(width * (double)Asset.Height / Asset.Width);
                if (width >= gray.Width || height >= gray.Height) continue;
                using var scaled = CreateScaledGray(width, height);
                var arrowRect = Support(width, height, 23d/45, 17d/46, 19d/45, 13d/46);
                using var arrow = new Mat(scaled, arrowRect);
                using var response = new Mat();
                Cv2.MatchTemplate(gray, arrow, response, TemplateMatchModes.CCoeffNormed); calls++;
                for (var i=0; i<3 && CanSearch(); i++)
                {
                    Cv2.MinMaxLoc(response, out _, out var score, out _, out var position);
                    if (!double.IsFinite(score) || score < .78) break;
                    seeds.Add((new(position.X-arrowRect.X, position.Y-arrowRect.Y), width, score));
                    Cv2.Rectangle(response, CreateSuppressionRect(position, arrow.Size(), response.Size()), Scalar.All(-1), -1);
                }
            }
            var gates = new List<GateDetection>();
            var evidence = new List<GateOcclusionEvidence>();
            foreach (var seed in seeds.OrderByDescending(s => s.Score).Take(6))
            {
                GateOcclusionEvidence? best = null;
                for (var width = Math.Max(minimumWidth,seed.Width-5); width<=Math.Min(maximumWidth,seed.Width+5) && CanSearch(); width++)
                {
                    var height=(int)Math.Round(width*(double)Asset.Height/Asset.Width);
                    using var colorTemplate=CreateScaledColor(width,height);
                    using var scaled=CreateMatchImage(colorTemplate);
                    var arrowRect=Support(width,height,23d/45,17d/46,19d/45,13d/46);
                    var search=new Rect(Math.Max(0,seed.Location.X-8),Math.Max(0,seed.Location.Y-8),
                        Math.Min(gray.Width-Math.Max(0,seed.Location.X-8),width+16),
                        Math.Min(gray.Height-Math.Max(0,seed.Location.Y-8),height+16));
                    if (search.Width<width || search.Height<height) continue;
                    using var area=new Mat(gray,search); using var arrow=new Mat(scaled,arrowRect); using var response=new Mat();
                    Cv2.MatchTemplate(area,arrow,response,TemplateMatchModes.CCoeffNormed); calls++;
                    Cv2.MinMaxLoc(response,out _,out var arrowScore,out _,out var position);
                    var x=search.X+position.X-arrowRect.X; var y=search.Y+position.Y-arrowRect.Y;
                    if (!double.IsFinite(arrowScore) || arrowScore<.88 || x<0 || y<0 || x+width>gray.Width || y+height>gray.Height) continue;
                    using var observed=new Mat(gray,new Rect(x,y,width,height));
                    // Disjoint from the proposal's arrow pixels: two horizontal frame supports.
                    if (!CanSearch()) break;
                    var upper=Correlation(scaled,observed,Support(width,height,23d/45,7d/46,12d/45,9d/46));
                    calls++;
                    if (!CanSearch()) break;
                    var lower=Correlation(scaled,observed,Support(width,height,23d/45,31d/46,12d/45,7d/46));
                    calls++;
                    if (!CanSearch()) break;
                    var whole=Correlation(scaled,observed,new Rect(0,0,width,height));
                    calls++;
                    var blue=BlueFraction(colorImage,new Rect(x,y,width/2,height),colorTemplate,new(x,y));
                    // Count blue pixels that also differ from the original color asset.
                    // The asset's own blue tint is not evidence of a player marker.
                    var confirmed=upper>=.85 && lower>=.85 && blue>=.12
                        && BlueFraction(colorImage,new Rect(x+width*2/3,y,width-width*2/3,height),colorTemplate,new(x,y))<.12;
                    var candidate=new GateOcclusionEvidence(new(viewport.X+x,viewport.Y+y,width,height),
                        arrowScore,upper,lower,whole,blue,confirmed,
                        confirmed ? "arrow-and-disjoint-frames-with-left-player-occlusion" : "independent-confirmation-failed");
                    if (best is null || (confirmed && !best.Confirmed)
                        || (confirmed==best.Confirmed && Math.Min(upper,lower)>Math.Min(best.UpperFrameScore,best.LowerFrameScore))) best=candidate;
                }
                if (best is null) continue;
                evidence.Add(best);
                if (best.Confirmed && CanSearch()) gates.Add(new()
                {
                    ScreenBounds=best.Bounds, Scale=best.Bounds.Width/Asset.Width,
                    Score=Math.Min(best.ArrowScore,Math.Min(best.UpperFrameScore,best.LowerFrameScore)),
                    EvidenceKind=GateEvidenceKind.PlayerOcclusionConfirmed, WholeTemplateScore=best.WholeScore
                });
                if (gates.Count>0) break;
            }
            var canceled=context.CancellationToken.IsCancellationRequested;
            var interrupted=!CanSearch();
            if (interrupted) gates.Clear(); // A deadline/cancellation cannot turn a partial search into acceptance.
            var selected=SelectTopCandidates(ClusterAcrossScales(gates));
            return new() { Asset=Asset, Gates=selected, RawCandidates=gates, OcclusionEvidence=evidence,
                MatchTemplateCalls=calls, ElapsedMilliseconds=timer.Elapsed.TotalMilliseconds,
                BudgetExceeded=interrupted && !canceled,
                StopReason=canceled ? GateSearchStopReason.Canceled : interrupted ? GateSearchStopReason.BudgetExceeded : GateSearchStopReason.Completed };
        }
    }

    private Mat CreateScaledGray(int width,int height)
    {
        using var scaled=CreateScaledColor(width,height);
        return CreateMatchImage(scaled);
    }
    private Mat CreateScaledColor(int width,int height)
    {
        var scaled=new Mat();
        try {Cv2.Resize(_gateSource,scaled,new(width,height),interpolation:InterpolationFlags.Area);return scaled;}
        catch {scaled.Dispose();throw;}
    }
    private static Rect Support(int width,int height,double x,double y,double w,double h) =>
        new((int)Math.Round(width*x),(int)Math.Round(height*y),Math.Max(2,(int)Math.Round(width*w)),Math.Max(2,(int)Math.Round(height*h)));
    private static double Correlation(Mat template,Mat observed,Rect rect)
    {
        using var t=new Mat(template,rect); using var o=new Mat(observed,rect);
        Cv2.MeanStdDev(t,out _,out var td); Cv2.MeanStdDev(o,out _,out var od);
        if (td.Val0<8 || od.Val0<8 || rect.Width*rect.Height<24) return -1;
        using var response=new Mat(); Cv2.MatchTemplate(o,t,response,TemplateMatchModes.CCoeffNormed);
        var value=(double)response.At<float>(0,0); return double.IsFinite(value) ? value : -1;
    }
    private static double BlueFraction(Mat color,Rect region,Mat expected,Point origin)
    {
        var count=0;
        for(var y=region.Y;y<region.Bottom;y++) for(var x=region.X;x<region.Right;x++)
        {
            var p=color.Channels()==3 ? color.At<Vec3b>(y,x) : new Vec3b(color.At<Vec4b>(y,x).Item0,color.At<Vec4b>(y,x).Item1,color.At<Vec4b>(y,x).Item2);
            var t=expected.Channels()==3 ? expected.At<Vec3b>(y-origin.Y,x-origin.X)
                : new Vec3b(expected.At<Vec4b>(y-origin.Y,x-origin.X).Item0,expected.At<Vec4b>(y-origin.Y,x-origin.X).Item1,
                    expected.At<Vec4b>(y-origin.Y,x-origin.X).Item2);
            if(p.Item0>70 && p.Item0-p.Item2>25 && p.Item1-p.Item2>10
                && Math.Abs(p.Item0-t.Item0)+Math.Abs(p.Item1-t.Item1)+Math.Abs(p.Item2-t.Item2)>=75) count++;
        }
        return (double)count/(region.Width*region.Height);
    }
}
