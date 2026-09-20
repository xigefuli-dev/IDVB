using System.Diagnostics;
using System.Text;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3VerificationToleranceTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3VerificationToleranceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark_VerificationTolerance_3x3_vs_5x5()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var extractedEdges = new Dictionary<string, Mat>();

        foreach (var sample in dataset)
        {
            var step = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(sample.LiveImage);
            extractedEdges[sample.Id] = step.Edges;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- [V-A STRICT GATE TOLERANCE COMPARISON: 3x3 vs 5x5 DILATION] ---");
            sb.AppendLine("| Kernel | Partition | Total Evals | Accepted | Correct Acc | Wrong Acc (FPR) | Precision | Fast-Path Cov | P50 (μs) |");
            sb.AppendLine("| :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var kernelSize in new[] { 3, 5 })
            {
                var verResults = new List<StrictVerificationResult>();
                foreach (var s in dataset)
                {
                    var qEdges = extractedEdges[s.Id];
                    var rEdges = s.ReferenceStructureLine;

                    // Nominal match
                    verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                        qEdges, rEdges, s.TrueScale, s.TrueOffsetX, s.TrueOffsetY, s, threshold: 0.50, kernelSize: kernelSize));

                    // False match (+25px, -35px)
                    verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                        qEdges, rEdges, s.TrueScale, s.TrueOffsetX + 25, s.TrueOffsetY + 25, s, threshold: 0.50, kernelSize: kernelSize));
                    verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                        qEdges, rEdges, s.TrueScale, s.TrueOffsetX - 35, s.TrueOffsetY - 35, s, threshold: 0.50, kernelSize: kernelSize));
                }

                var partitions = new[] { "Real-Only", "Synthetic-Only", "Combined" };
                foreach (var part in partitions)
                {
                    var items = part switch
                    {
                        "Real-Only" => verResults.Where(r => r.SourceType == "RealMap").ToList(),
                        "Synthetic-Only" => verResults.Where(r => r.SourceType == "Synthetic").ToList(),
                        _ => verResults
                    };
                    if (items.Count == 0) continue;

                    var total = items.Count;
                    var accepted = items.Where(r => r.Accepted).ToList();
                    var accCount = accepted.Count;
                    var correctAcc = accepted.Count(r => r.IsActuallyCorrect);
                    var wrongAcc = accepted.Count(r => !r.IsActuallyCorrect);
                    var accPrec = accCount > 0 ? (double)correctAcc / accCount * 100.0 : 100.0;

                    var truePositives = items.Count(r => r.IsActuallyCorrect);
                    var fastPathCov = truePositives > 0 ? (double)correctAcc / truePositives * 100.0 : 0.0;

                    var lats = items.Select(r => r.ElapsedMicroseconds).OrderBy(x => x).ToList();
                    var latP50 = lats[lats.Count / 2];

                    sb.AppendLine($"| {kernelSize}x{kernelSize} | {part,-14} | {total,11} | {accCount,8} | {correctAcc,11} | {wrongAcc,15} | {accPrec,8:F1}% | {fastPathCov,12:F1}% | {latP50,8:F1} |");
                }
            }

            _output.WriteLine(sb.ToString());
        }
        finally
        {
            foreach (var kv in extractedEdges)
            {
                kv.Value.Dispose();
            }
            foreach (var s in dataset)
            {
                s.Dispose();
            }
        }
    }
}
