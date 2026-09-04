using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase3ADiagnostics
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase3ADiagnostics(ITestOutputHelper output)
    {
        _output = output;
    }

    public sealed record CandidateDetail(
        int Rank,
        double OffsetX,
        double OffsetY,
        double TranslationError,
        double VaRawScore,
        bool IsActuallyCorrect);

    public sealed record WrongAcceptRecord(
        string SampleId,
        string SourceType,
        double TrueScale,
        double EstimatedScale,
        double ScaleError,
        double SbPeakRatio,
        bool SbGatePassed,
        bool UsedSeFallback,
        int EdgePixelCount,
        int ValidPixelCount,
        List<CandidateDetail> Candidates,
        int AcceptedRank,
        double AcceptedTransError,
        double AcceptedVaScore,
        bool TrueInTop4,
        string RootCauseCategory,
        string ReasonSummary);

    [Fact]
    public void Diagnose_AllWrongAccepts_InOriginalRehearsal()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var wrongRecords = new List<WrongAcceptRecord>();
            var allRecords = new List<(string Id, bool Accepted, bool ActuallyCorrect)>();

            foreach (var s in dataset)
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var qEdges = obs.ObservedEdges;

                // Replicate original rehearsal logic exactly:
                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                    qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);

                var sbGatePassed = peakRatio >= 2.0d;
                double estimatedScale;
                var usedSeFallback = false;

                if (sbGatePassed)
                {
                    estimatedScale = sbRes.EstimatedScale;
                }
                else
                {
                    usedSeFallback = true;
                    var seRes = Vpsg3ScalePrototypes.EvaluateScaleMethodE(
                        qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                    estimatedScale = seRes.EstimatedScale;
                }

                var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                    qEdges, s.ReferenceStructureLine, s, estimatedScale, topK: 4);

                var candDetails = new List<CandidateDetail>();
                var acceptedRank = -1;
                double acceptedX = double.NaN;
                double acceptedY = double.NaN;
                double acceptedScore = 0d;
                var accepted = false;

                for (var i = 0; i < candidates.Count; i++)
                {
                    var cand = candidates[i];
                    var ver = Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                        qEdges, s.ReferenceStructureLine, estimatedScale, cand.OffsetX, cand.OffsetY, s);

                    var transErr = Math.Sqrt(Math.Pow(cand.OffsetX - s.TrueOffsetX, 2) + Math.Pow(cand.OffsetY - s.TrueOffsetY, 2));
                    var scaleErr = Math.Abs(estimatedScale - s.TrueScale);
                    var isCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

                    candDetails.Add(new CandidateDetail(
                        Rank: i + 1,
                        OffsetX: cand.OffsetX,
                        OffsetY: cand.OffsetY,
                        TranslationError: transErr,
                        VaRawScore: ver.Score,
                        IsActuallyCorrect: isCorrect));

                    if (!accepted && ver.Accepted)
                    {
                        accepted = true;
                        acceptedRank = i + 1;
                        acceptedX = cand.OffsetX;
                        acceptedY = cand.OffsetY;
                        acceptedScore = ver.Score;
                    }
                }

                var totalScaleError = Math.Abs(estimatedScale - s.TrueScale);
                var finalTransErr = accepted ? Math.Sqrt(Math.Pow(acceptedX - s.TrueOffsetX, 2) + Math.Pow(acceptedY - s.TrueOffsetY, 2)) : double.NaN;
                var finalActuallyCorrect = accepted && totalScaleError <= 0.035d && finalTransErr <= 4.0d;

                allRecords.Add((s.Id, accepted, finalActuallyCorrect));

                if (accepted && !finalActuallyCorrect)
                {
                    var trueInTop4 = candDetails.Any(c => c.IsActuallyCorrect);

                    // Classify root cause:
                    string cat;
                    string reason;

                    if (totalScaleError > 0.035d)
                    {
                        cat = "A (Scale Error)";
                        reason = $"Scale error ({totalScaleError:F4}) shifted all candidate positions. S-B PeakRatio={peakRatio:F2}, UsedSE={usedSeFallback}";
                    }
                    else if (trueInTop4 && acceptedRank > 1 && candDetails.First(c => c.IsActuallyCorrect).Rank > acceptedRank)
                    {
                        cat = "C (Correct in Top4, but inferior wrong rank accepted first)";
                        var correctRank = candDetails.First(c => c.IsActuallyCorrect).Rank;
                        reason = $"Correct candidate was Rank {correctRank}, but Rank {acceptedRank} hit V-A threshold ({acceptedScore:F3} >= 0.52) first.";
                    }
                    else if (!trueInTop4)
                    {
                        cat = "D (Wrong Candidate Passed by Lenient V-A)";
                        reason = $"True offset not in Top4; wrong candidate Rank {acceptedRank} erroneously scored {acceptedScore:F3} >= 0.52.";
                    }
                    else
                    {
                        cat = "F (Other)";
                        reason = $"TransErr={finalTransErr:F1}px, ScaleErr={totalScaleError:F4}";
                    }

                    wrongRecords.Add(new WrongAcceptRecord(
                        SampleId: s.Id,
                        SourceType: s.SourceType,
                        TrueScale: s.TrueScale,
                        EstimatedScale: estimatedScale,
                        ScaleError: totalScaleError,
                        SbPeakRatio: peakRatio,
                        SbGatePassed: sbGatePassed,
                        UsedSeFallback: usedSeFallback,
                        EdgePixelCount: obs.EdgePixelCount,
                        ValidPixelCount: obs.ValidStructurePixelCount,
                        Candidates: candDetails,
                        AcceptedRank: acceptedRank,
                        AcceptedTransError: finalTransErr,
                        AcceptedVaScore: acceptedScore,
                        TrueInTop4: trueInTop4,
                        RootCauseCategory: cat,
                        ReasonSummary: reason));
                }
            }

            // Print full table of wrong accept records
            var sb = new StringBuilder();
            sb.AppendLine("\n==========================================================================================================");
            sb.AppendLine($"           VPSG 3.0 PHASE 3A DIAGNOSTIC: ALL {wrongRecords.Count} WRONG ACCEPTS RAW BREAKDOWN             ");
            sb.AppendLine("==========================================================================================================");
            sb.AppendLine("| # | SampleId | Type | TrueScale | EstScale | ScaleErr | S-B Peak | UsedSE | Top4 HasGT? | AccRank | AccTransErr | AccScore | Root Cause |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |");

            for (var idx = 0; idx < wrongRecords.Count; idx++)
            {
                var r = wrongRecords[idx];
                sb.AppendLine($"| {idx + 1,2} | {r.SampleId,-24} | {r.SourceType,-9} | {r.TrueScale,9:F3} | {r.EstimatedScale,8:F3} | {r.ScaleError,8:F4} | {r.SbPeakRatio,8:F2} | {r.UsedSeFallback,6} | {r.TrueInTop4,11} | {r.AcceptedRank,7} | {r.AcceptedTransError,9:F1}px | {r.AcceptedVaScore,8:F3} | {r.RootCauseCategory} |");
            }

            // Root cause distribution
            sb.AppendLine("\n----------------------------------------------------------------------------------------------------------");
            sb.AppendLine("                               ROOT CAUSE DISTRIBUTION SUMMARY                                            ");
            sb.AppendLine("----------------------------------------------------------------------------------------------------------");
            var groups = wrongRecords.GroupBy(r => r.RootCauseCategory).OrderByDescending(g => g.Count());
            foreach (var g in groups)
            {
                var pct = (double)g.Count() / wrongRecords.Count * 100.0;
                sb.AppendLine($" - {g.Key}: {g.Count()} samples ({pct:F1}%)");
            }

            // Candidate breakdown for each wrong accept
            sb.AppendLine("\n----------------------------------------------------------------------------------------------------------");
            sb.AppendLine("                               PER-SAMPLE DETAILED CANDIDATES                                             ");
            sb.AppendLine("----------------------------------------------------------------------------------------------------------");
            foreach (var r in wrongRecords)
            {
                sb.AppendLine($"\n>>> [{r.SampleId}] ({r.SourceType}) TrueScale={r.TrueScale:F3}, EstScale={r.EstimatedScale:F3}, ScaleErr={r.ScaleError:F4}, S-B Peak={r.SbPeakRatio:F2}, UsedSE={r.UsedSeFallback}");
                sb.AppendLine($"    Diagnosis: {r.ReasonSummary}");
                sb.AppendLine("    Top-K Candidates:");
                foreach (var c in r.Candidates)
                {
                    var isAcc = c.Rank == r.AcceptedRank ? " [ACCEPTED]" : "";
                    var isGt = c.IsActuallyCorrect ? " [IS_TRUE_GT]" : "";
                    sb.AppendLine($"      Rank {c.Rank}: Offset=({c.OffsetX:F1}, {c.OffsetY:F1}), TransErr={c.TranslationError:F1}px, VA_Score={c.VaRawScore:F3}{isAcc}{isGt}");
                }
            }

            var outText = sb.ToString();
            _output.WriteLine(outText);
            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                File.WriteAllText(Path.Combine(scratchDir, "diagnostics_wrong_accepts.txt"), outText);
            }
            catch { }
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }

    [Fact]
    public void Diagnose_All21WithRefinement()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("| SampleId | TrueScale | EstScale -> RefScale | ScaleErr | Top1 TransErr -> RefTransErr | Score | SpatialOk | PassedGate? |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

            foreach (var s in dataset)
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                    obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);

                if (peakRatio < 2.0d)
                {
                    sb.AppendLine($"| {s.Id,-24} | {s.TrueScale:F3} | REJECTED_BY_SB_GATE (Peak={peakRatio:F2}) | - | - | - | - | REJECT |");
                    continue;
                }

                var cands = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                    obs.ObservedEdges, s.ReferenceStructureLine, s, sbRes.EstimatedScale, topK: 4);
                if (cands.Count == 0)
                {
                    sb.AppendLine($"| {s.Id,-24} | {s.TrueScale:F3} | NO_CANDIDATES | - | - | - | - | REJECT |");
                    continue;
                }

                using var refDilatedK5 = new Mat();
                Cv2.Dilate(s.ReferenceStructureLine, refDilatedK5, k5);

                using var refDilatedK3 = new Mat();
                Cv2.Dilate(s.ReferenceStructureLine, refDilatedK3, k3);

                var refinedCands = new List<(double Scale, double X, double Y, double Score, Vpsg3Phase3ACorrectnessSuite.SpatialVerificationResult Spatial)>();

                foreach (var cand in cands)
                {
                    var (rfScale, rfX, rfY, rfScore) = Vpsg3Phase3ACorrectnessSuite.LocalRefineScaleAndTranslation(
                        obs.SparseEdgePoints, refDilatedK5, refDilatedK3, sbRes.EstimatedScale, cand.OffsetX, cand.OffsetY,
                        s.ViewportBounds, obs.Width, obs.Height);

                    var sp = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                        obs.SparseEdgePoints, refDilatedK5, rfScale, rfX, rfY,
                        s.ViewportBounds, obs.Width, obs.Height, minPartitionsRequired: 2);

                    refinedCands.Add((rfScale, rfX, rfY, rfScore, sp));
                }

                // Sort by refined score descending
                refinedCands.Sort((a, b) => b.Score.CompareTo(a.Score));

                var best = refinedCands[0];
                // Find second best that is spatially distinct (> 6.0px apart)
                var secondBestScore = 0.0d;
                for (var i = 1; i < refinedCands.Count; i++)
                {
                    var dist = Math.Sqrt(Math.Pow(refinedCands[i].X - best.X, 2) + Math.Pow(refinedCands[i].Y - best.Y, 2));
                    if (dist > 6.0d)
                    {
                        secondBestScore = refinedCands[i].Score;
                        break;
                    }
                }

                var margin = best.Score - secondBestScore;
                var postScaleErr = Math.Abs(best.Scale - s.TrueScale);
                var postTransErr = Math.Sqrt(Math.Pow(best.X - s.TrueOffsetX, 2) + Math.Pow(best.Y - s.TrueOffsetY, 2));
                var isActuallyCorrect = postScaleErr <= 0.035d && postTransErr <= 4.0d;

                var gatePass = best.Score >= 0.65d && margin >= 0.04d && best.Spatial.PassedPartitions >= 3;

                sb.AppendLine($"| {s.Id,-24} | {s.TrueScale:F3} | {sbRes.EstimatedScale:F3} -> {best.Scale:F3} | {postScaleErr:F4} | {postTransErr,5:F1}px | Sc={best.Score:F3} | Mg={margin:F3} | SpP={best.Spatial.PassedPartitions} | {(gatePass ? (isActuallyCorrect ? "PASS_CORRECT" : "PASS_WRONG!") : (isActuallyCorrect ? "REJECT_GOOD" : "REJECT_BAD"))} |");
            }

            var outText = sb.ToString();
            _output.WriteLine(outText);
            var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
            File.WriteAllText(Path.Combine(scratchDir, "diagnose_21_refined.txt"), outText);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Diagnose_Sample035()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var s = dataset.First(d => d.Id.StartsWith("syn_035"));
            using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
            using var refD5 = new Mat();
            using var refD3 = new Mat();
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(s.ReferenceStructureLine, refD5, k5);
            Cv2.Dilate(s.ReferenceStructureLine, refD3, k3);

            using var liveDilated = new Mat();
            Cv2.Dilate(obs.ObservedEdges, liveDilated, k5);

            var sb = new StringBuilder();

            // Check syn_035 candidate (Refined: scale=1.1183, X=153.0, Y=97.3)
            var (revRefined, fwdRefined) = ComputeBidirectionalScores(
                obs.SparseEdgePoints, s.ReferenceStructureLine, liveDilated, refD5,
                1.1183, 153.0, 97.3, s.ViewportBounds, obs.Width, obs.Height);
            sb.AppendLine($"Wrong Match (syn_035): FwdScore={fwdRefined:F3}, RevScore={revRefined:F3}");

            // Check Ground Truth (scale=1.180, X=125, Y=86)
            var (revGt, fwdGt) = ComputeBidirectionalScores(
                obs.SparseEdgePoints, s.ReferenceStructureLine, liveDilated, refD5,
                1.180, 125.0, 86.0, s.ViewportBounds, obs.Width, obs.Height);
            sb.AppendLine($"Ground Truth: FwdScore={fwdGt:F3}, RevScore={revGt:F3}");

            var outText = sb.ToString();
            _output.WriteLine(outText);
            var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
            File.WriteAllText(Path.Combine(scratchDir, "diagnose_sample035.txt"), outText);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    private static (double ReverseScore, double ForwardScore) ComputeBidirectionalScores(
        IReadOnlyList<Point> livePoints,
        Mat refStructure,
        Mat liveDilated,
        Mat refDilated,
        double scale,
        double offsetX,
        double offsetY,
        MapScreenRect viewport,
        int width,
        int height)
    {
        // 1. Forward score: live points hitting dilated reference
        var fwdHits = 0;
        foreach (var p in livePoints)
        {
            var sx = viewport.X + p.X;
            var sy = viewport.Y + p.Y;
            var rx = (int)Math.Round((sx - offsetX) / scale);
            var ry = (int)Math.Round((sy - offsetY) / scale);
            if (rx >= 0 && rx < refDilated.Width && ry >= 0 && ry < refDilated.Height)
            {
                if (refDilated.At<byte>(ry, rx) > 128) fwdHits++;
            }
        }
        var fwdScore = (double)fwdHits / livePoints.Count;

        // 2. Reverse score: reference points inside live viewport hitting dilated live edges
        var revTotal = 0;
        var revHits = 0;
        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                var sx = viewport.X + x;
                var sy = viewport.Y + y;
                var rx = (int)Math.Round((sx - offsetX) / scale);
                var ry = (int)Math.Round((sy - offsetY) / scale);

                if (rx >= 0 && rx < refStructure.Width && ry >= 0 && ry < refStructure.Height)
                {
                    if (refStructure.At<byte>(ry, rx) > 128)
                    {
                        revTotal++;
                        if (liveDilated.At<byte>(y, x) > 128) revHits++;
                    }
                }
            }
        }
        var revScore = revTotal > 0 ? (double)revHits / revTotal : 0.0d;
        return (revScore, fwdScore);
    }
}
