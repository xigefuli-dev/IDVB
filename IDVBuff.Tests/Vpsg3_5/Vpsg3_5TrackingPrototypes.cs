using System.Diagnostics;
using System.Runtime.CompilerServices;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3_5;

public readonly record struct Vpsg3_5TrackingBenchmarkPoint(
    int Step,
    double TrueOffsetX,
    double TrueOffsetY,
    double EstimatedOffsetX,
    double EstimatedOffsetY,
    double ErrorPixels,
    double LatencyMs,
    bool IsLocalFastPath,
    bool Accepted);

public readonly record struct Vpsg3_5TrackingSummary(
    int TotalFrames,
    int LocalFastPathCount,
    int FallbackCount,
    int AcceptedCount,
    double P50LatencyMs,
    double P95LatencyMs,
    double MaxLatencyMs,
    double MaxErrorPixels,
    double MeanErrorPixels,
    bool ZeroDriftVerified);

public static class Vpsg3_5TrackingPrototypes
{
    /// <summary>
    /// Evaluates a localized translation search using resident bitsets around a prior (priorTx, priorTy).
    /// Highly optimized with direct bitset span lookups.
    /// </summary>
    public static (double OffsetX, double OffsetY, int Hits, double Score, bool Succeeded, double LatencyMs) EvaluateLocalWindowTranslation(
        IReadOnlyList<Point> sparsePoints,
        Vpsg3PreparedFloor preparedFloor,
        double lockedScale,
        double priorTx,
        double priorTy,
        MapScreenRect viewportBounds,
        int searchRadius = 36,
        int stride = 2,
        double minScoreThreshold = 0.35d)
    {
        var sw = Stopwatch.StartNew();
        if (sparsePoints.Count == 0 || preparedFloor.IsDisposed || lockedScale <= 0)
        {
            sw.Stop();
            return (priorTx, priorTy, 0, 0d, false, sw.Elapsed.TotalMilliseconds);
        }

        var invScale = 1.0d / lockedScale;
        var scaledCount = Math.Min(sparsePoints.Count, 120);
        Span<Point> scaledPts = stackalloc Point[scaledCount];
        for (var i = 0; i < scaledCount; i++)
        {
            var p = sparsePoints[i];
            scaledPts[i] = new Point(
                (int)Math.Round(p.X * invScale),
                (int)Math.Round(p.Y * invScale));
        }

        var centerDx = (int)Math.Round((viewportBounds.X - priorTx) * invScale);
        var centerDy = (int)Math.Round((viewportBounds.Y - priorTy) * invScale);

        var radiusRef = (int)Math.Ceiling(searchRadius * invScale);
        var minDx = centerDx - radiusRef;
        var maxDx = centerDx + radiusRef;
        var minDy = centerDy - radiusRef;
        var maxDy = centerDy + radiusRef;

        var wordsK3 = preparedFloor.DilatedBitsetK3Span;
        var wordsPerRow = preparedFloor.WordsPerRow;
        var refW = (uint)preparedFloor.ReferenceWidth;
        var refH = (uint)preparedFloor.ReferenceHeight;

        var bestDx = centerDx;
        var bestDy = centerDy;
        var bestHits = 0;

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                for (var i = 0; i < scaledCount; i++)
                {
                    var rx = (uint)(scaledPts[i].X + dx);
                    var ry = (uint)(scaledPts[i].Y + dy);
                    if (rx < refW && ry < refH)
                    {
                        var wordIdx = (int)ry * wordsPerRow + (int)(rx >> 6);
                        if ((wordsK3[wordIdx] & (1UL << (int)(rx & 63))) != 0)
                        {
                            hits++;
                        }
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        // Sub-pixel 3x3 Polish (+/-1px around bestDx, bestDy)
        var polishedDx = bestDx;
        var polishedDy = bestDy;
        var polishedHits = bestHits;
        for (var fdy = -1; fdy <= 1; fdy++)
        {
            for (var fdx = -1; fdx <= 1; fdx++)
            {
                if (fdx == 0 && fdy == 0) continue;
                var testDx = bestDx + fdx;
                var testDy = bestDy + fdy;
                var testHits = 0;
                for (var i = 0; i < scaledCount; i++)
                {
                    var rx = (uint)(scaledPts[i].X + testDx);
                    var ry = (uint)(scaledPts[i].Y + testDy);
                    if (rx < refW && ry < refH)
                    {
                        var wordIdx = (int)ry * wordsPerRow + (int)(rx >> 6);
                        if ((wordsK3[wordIdx] & (1UL << (int)(rx & 63))) != 0)
                        {
                            testHits++;
                        }
                    }
                }
                if (testHits > polishedHits)
                {
                    polishedHits = testHits;
                    polishedDx = testDx;
                    polishedDy = testDy;
                }
            }
        }

        var estOffsetX = viewportBounds.X - polishedDx * lockedScale;
        var estOffsetY = viewportBounds.Y - polishedDy * lockedScale;
        sw.Stop();

        var score = (double)polishedHits / scaledCount;
        var success = score >= minScoreThreshold && polishedHits >= 10;
        return (estOffsetX, estOffsetY, polishedHits, score, success, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Simulates a 100-frame continuous tracking sequence over a ground truth reference.
    /// Injects realistic trajectory motion including smooth drift and sudden step jumps.
    /// </summary>
    public static Vpsg3_5TrackingSummary RunContinuousTrackingSimulation(
        GroundTruthSample sample,
        Vpsg3PreparedFloor preparedFloor,
        int totalFrames = 100)
    {
        using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
        var basePoints = obs.SparseEdgePoints;
        var bounds = sample.ViewportBounds;
        var scale = sample.TrueScale;
        var initialTx = sample.TrueOffsetX;
        var initialTy = sample.TrueOffsetY;

        var currentPriorTx = initialTx;
        var currentPriorTy = initialTy;

        var latencies = new List<double>(totalFrames);
        var errors = new List<double>(totalFrames);
        var localFastCount = 0;
        var fallbackCount = 0;
        var acceptedCount = 0;

        var rand = new Random(42);
        var cumTrueDx = 0d;
        var cumTrueDy = 0d;

        for (var step = 0; step < totalFrames; step++)
        {
            // Simulate frame-to-frame delta: mostly +/-2px to +/-6px, with step 40 having a 65px sudden flick
            double deltaX;
            double deltaY;
            if (step == 40)
            {
                deltaX = 65.0d; // Sudden flick jump exceeding standard search radius
                deltaY = -40.0d;
            }
            else
            {
                deltaX = (rand.NextDouble() * 8.0d) - 4.0d;
                deltaY = (rand.NextDouble() * 6.0d) - 3.0d;
            }

            cumTrueDx += deltaX;
            cumTrueDy += deltaY;

            var trueStepTx = initialTx + cumTrueDx;
            var trueStepTy = initialTy + cumTrueDy;

            // Generate shifted sparse points simulating moved viewport:
            var shiftedPoints = new List<Point>(basePoints.Count);
            var shiftScreenX = (int)Math.Round(cumTrueDx);
            var shiftScreenY = (int)Math.Round(cumTrueDy);

            for (var i = 0; i < basePoints.Count; i++)
            {
                var bp = basePoints[i];
                var sx = bp.X + shiftScreenX;
                var sy = bp.Y + shiftScreenY;
                if (sx >= 0 && sx < obs.Width && sy >= 0 && sy < obs.Height)
                {
                    shiftedPoints.Add(new Point(sx, sy));
                }
            }

            if (shiftedPoints.Count < 20)
            {
                shiftedPoints = [.. basePoints];
            }

            // Execute local window search
            var (estTx, estTy, hits, score, localOk, latency) = EvaluateLocalWindowTranslation(
                shiftedPoints,
                preparedFloor,
                scale,
                currentPriorTx,
                currentPriorTy,
                bounds,
                searchRadius: 36,
                stride: 2,
                minScoreThreshold: 0.35d);

            double candidateTx;
            double candidateTy;

            if (localOk)
            {
                localFastCount++;
                candidateTx = estTx;
                candidateTy = estTy;
            }
            else
            {
                // Fallback to global translation search with fixed scale
                fallbackCount++;
                // Create temporary observation for global translation solver
                using var shiftedObs = new Vpsg3LiveObservation(
                    obs.ObservedEdges,
                    obs.ValidMask,
                    obs.Width,
                    obs.Height,
                    obs.EdgePixelCount,
                    obs.ValidStructurePixelCount,
                    bounds,
                    sparseEdgePoints: shiftedPoints.ToArray());

                var (top1, _, _, _) = Vpsg3TranslationSolver.GenerateCandidates(
                    shiftedObs,
                    preparedFloor,
                    scale);

                candidateTx = top1.OffsetX;
                candidateTy = top1.OffsetY;
            }

            // Local refinement with locked scale
            var (_, refX, refY, refScore, _) = Vpsg3LocalRefiner.Refine(
                shiftedPoints,
                preparedFloor,
                scale,
                candidateTx,
                candidateTy,
                bounds,
                obs.Width,
                obs.Height,
                lockScale: true);

            var finalTx = refX;
            var finalTy = refY;

            var errX = finalTx - trueStepTx;
            var errY = finalTy - trueStepTy;
            var errDist = Math.Sqrt(errX * errX + errY * errY);

            errors.Add(errDist);
            latencies.Add(latency);
            if (errDist <= 3.5d)
            {
                acceptedCount++;
            }

            // Update prior for next frame
            currentPriorTx = finalTx;
            currentPriorTy = finalTy;
        }

        latencies.Sort();
        var p50 = latencies[(int)(totalFrames * 0.50)];
        var p95 = latencies[(int)(totalFrames * 0.95)];
        var maxLat = latencies[^1];
        var maxErr = errors.Max();
        var meanErr = errors.Average();

        var first20Err = errors.Take(20).Average();
        var last20Err = errors.Skip(totalFrames - 20).Take(20).Average();
        var zeroDrift = Math.Abs(last20Err - first20Err) <= 1.0d && maxErr <= 4.0d;

        return new Vpsg3_5TrackingSummary(
            TotalFrames: totalFrames,
            LocalFastPathCount: localFastCount,
            FallbackCount: fallbackCount,
            AcceptedCount: acceptedCount,
            P50LatencyMs: p50,
            P95LatencyMs: p95,
            MaxLatencyMs: maxLat,
            MaxErrorPixels: maxErr,
            MeanErrorPixels: meanErr,
            ZeroDriftVerified: zeroDrift);
    }
}
