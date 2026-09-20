using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapAnchorTrackerTests
{
    [Fact]
    public void DistinctGateContextResolvesSingleVisibleGate()
    {
        using var reference = new Mat(
            new Size(300, 200),
            MatType.CV_8UC3,
            Scalar.All(0));
        Cv2.Rectangle(reference, new Rect(42, 82, 36, 36), Scalar.White, 3);
        Cv2.Line(reference, new Point(44, 114), new Point(75, 84), Scalar.White, 2);
        Cv2.Circle(reference, new Point(240, 100), 18, Scalar.White, 3);
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var profile = map.Recognition.FirstFloor;
        profile.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.16d, Y = 0.44d, Width = 0.08d, Height = 0.12d };
        profile.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.76d, Y = 0.44d, Width = 0.08d, Height = 0.12d };
        var fingerprint = new MapGeometryFingerprint
        {
            Map = map,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height
        };
        using var live = new Mat(
            new Size(500, 340),
            MatType.CV_8UC3,
            Scalar.All(0));
        const int offsetX = 75;
        const int offsetY = 55;
        using (var destination = new Mat(
                   live,
                   new Rect(offsetX, offsetY, reference.Width, reference.Height)))
        {
            reference.CopyTo(destination);
        }
        var viewport = new MapScreenRect(800d, 400d, live.Width, live.Height);
        var gate = new GateDetection
        {
            Score = 0.95d,
            Scale = 0.4d,
            ScreenBounds = new MapScreenRect(
                viewport.X + offsetX + 48d,
                viewport.Y + offsetY + 88d,
                24d,
                24d)
        };

        var resolved = MapAnchorTracker.TryResolveSingleGate(
            reference,
            live,
            fingerprint,
            gate,
            viewport,
            LockedTransform(reference),
            minimumConfidence: 0.50d,
            minimumAdvantage: 0.08d,
            out var evidence,
            out var failureReason);

        Assert.True(resolved, failureReason);
        Assert.Equal(profile.FindAnchor("main-entrance")!.Id, evidence.AnchorId);
    }

    [Fact]
    public void AmbiguousSingleGateFailureDoesNotRequestAuxiliaryAnchors()
    {
        using var reference = new Mat(
            new Size(300, 200),
            MatType.CV_8UC3,
            Scalar.All(0));
        Cv2.Rectangle(reference, new Rect(42, 82, 36, 36), Scalar.White, 3);
        Cv2.Rectangle(reference, new Rect(222, 82, 36, 36), Scalar.White, 3);
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var profile = map.Recognition.FirstFloor;
        profile.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.16d, Y = 0.44d, Width = 0.08d, Height = 0.12d };
        profile.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.76d, Y = 0.44d, Width = 0.08d, Height = 0.12d };
        var fingerprint = new MapGeometryFingerprint
        {
            Map = map,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height
        };
        using var live = reference.Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var gate = new GateDetection
        {
            Score = 0.95d,
            Scale = 0.4d,
            ScreenBounds = new MapScreenRect(48d, 88d, 24d, 24d)
        };

        var resolved = MapAnchorTracker.TryResolveSingleGate(
            reference,
            live,
            fingerprint,
            gate,
            viewport,
            LockedTransform(reference),
            minimumConfidence: 0.50d,
            minimumAdvantage: 0.08d,
            out _,
            out var failureReason);

        Assert.False(resolved);
        Assert.DoesNotContain("辅助锚点", failureReason);
        Assert.DoesNotContain("等待更多锚点", failureReason);
    }

    [Fact]
    public void UniqueAuxiliaryAnchorsRecoverConsistentTranslation()
    {
        using var reference = BuildReference(out var fingerprint);
        using var live = new Mat(new Size(440, 320), MatType.CV_8UC3, Scalar.All(0));
        var destination = new Rect(70, 45, reference.Width, reference.Height);
        using (var destinationImage = new Mat(live, destination))
            reference.CopyTo(destinationImage);
        var viewport = new MapScreenRect(900d, 500d, live.Width, live.Height);
        var locked = LockedTransform(reference);

        var result = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            viewport,
            locked,
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d);

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.Equal(2, result.Matches.Count);
        Assert.True(MapOverlayTransformSolver.TryTranslateWithLockedScale(
            locked,
            result.Matches,
            out var transform,
            out var failureReason),
            failureReason);
        Assert.Equal(viewport.X + destination.X, transform.OffsetX, 1);
        Assert.Equal(viewport.Y + destination.Y, transform.OffsetY, 1);
    }

    [Fact]
    public void RepeatedAuxiliaryTextureIsRejectedAsAmbiguous()
    {
        using var reference = BuildReference(out var fingerprint);
        fingerprint.Map.Recognition.FirstFloor.Anchors.RemoveAll(
            anchor => anchor.Role == RecognitionAnchorRole.Optional
                && anchor.DisplayName == "triangle");
        var optional = fingerprint.Map.Recognition.FirstFloor.Anchors.Single(
            anchor => anchor.Role == RecognitionAnchorRole.Optional);
        var referenceBounds = optional.Bounds!;
        var sourceRect = new Rect(
            (int)Math.Round(referenceBounds.X * reference.Width),
            (int)Math.Round(referenceBounds.Y * reference.Height),
            (int)Math.Round(referenceBounds.Width * reference.Width),
            (int)Math.Round(referenceBounds.Height * reference.Height));
        using var patch = new Mat(reference, sourceRect);
        using var live = new Mat(new Size(360, 240), MatType.CV_8UC3, Scalar.All(0));
        using (var first = new Mat(
                   live,
                   new Rect(35, 45, patch.Width, patch.Height)))
        {
            patch.CopyTo(first);
        }
        using (var second = new Mat(
                   live,
                   new Rect(235, 145, patch.Width, patch.Height)))
        {
            patch.CopyTo(second);
        }

        var result = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform(reference),
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void OneUniqueAuxiliaryAnchorDoesNotFormDirectConsensus()
    {
        using var reference = BuildReference(out var fingerprint);
        fingerprint.Map.Recognition.FirstFloor.Anchors.RemoveAll(
            anchor => anchor.Role == RecognitionAnchorRole.Optional
                && anchor.DisplayName == "triangle");
        using var live = new Mat(
            new Size(440, 320),
            MatType.CV_8UC3,
            Scalar.All(0));
        using (var destination = new Mat(
                   live,
                   new Rect(70, 45, reference.Width, reference.Height)))
        {
            reference.CopyTo(destination);
        }

        var result = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform(reference),
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d);

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.Single(result.Matches);
        Assert.False(result.HasIndependentConsensus);
        Assert.True(result.Confidence < 0.82d);
    }

    [Fact]
    public void ConflictingAuxiliaryOffsetsAreRejected()
    {
        // 使用独立 reference（无装饰线），避免连线穿过锚点模板区域导致全域搜索匹配失败。
        using var reference = BuildCleanReference(out var fingerprint);
        using var live = new Mat(
            new Size(440, 320),
            MatType.CV_8UC3,
            Scalar.All(0));
        using (var box = new Mat(reference, new Rect(24, 30, 32, 30)))
        using (var destination = new Mat(live, new Rect(94, 75, 32, 30)))
        {
            box.CopyTo(destination);
        }
        using (var circle = new Mat(reference, new Rect(168, 110, 32, 32)))
        using (var destination = new Mat(live, new Rect(280, 190, 32, 32)))
        {
            circle.CopyTo(destination);
        }

        var result = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform(reference),
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d);

        Assert.False(result.IsSuccess);
        Assert.Contains("不一致", result.FailureReason);
    }

    [Fact]
    public void ManyAuxiliaryAnchorsStillEvaluateAndCacheAtMostEightTemplates()
    {
        using var reference = BuildReference(out var fingerprint);
        var profile = fingerprint.Map.Recognition.FirstFloor;
        var boxBounds = profile.Anchors.Single(anchor =>
            anchor.DisplayName == "box").Bounds!;
        for (var index = 0; index < 10; index++)
        {
            profile.Anchors.Add(new RecognitionAnchor
            {
                Key = $"optional-extra-{index}",
                DisplayName = $"extra {index}",
                Role = RecognitionAnchorRole.Optional,
                Weight = 0.95d - (index * 0.01d),
                Bounds = boxBounds.Clone()
            });
        }
        using var live = new Mat(
            new Size(440, 320),
            MatType.CV_8UC3,
            Scalar.All(0));
        using (var destination = new Mat(
                   live,
                   new Rect(70, 45, reference.Width, reference.Height)))
        {
            reference.CopyTo(destination);
        }
        using var cache = new MapAuxiliaryAnchorTemplateCache();

        var first = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform(reference),
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d,
            maximumTemplates: 99,
            templateCache: cache);
        var second = MapAnchorTracker.TrackAuxiliaryAnchors(
            reference,
            live,
            fingerprint,
            new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform(reference),
            minimumScore: 0.70d,
            minimumAdvantage: 0.08d,
            maximumTemplates: 99,
            templateCache: cache);

        Assert.Equal(8, first.TemplatesEvaluated);
        Assert.Equal(8, second.TemplatesEvaluated);
        Assert.Equal(8, cache.CachedTemplateCount);
    }

    private static Mat BuildCleanReference(out MapGeometryFingerprint fingerprint)
    {
        // 粗描边(5px)使 Canny 双边缘经 morph close 合并为厚边缘，避免被 morph open 抹除，
        // 从而在全域 CCOEFF_NORMED 搜索中达到 >= 0.78 的分数。
        var reference = new Mat(new Size(240, 180), MatType.CV_8UC3, Scalar.All(0));

        Cv2.Rectangle(reference, new Rect(24, 30, 32, 30), Scalar.White, 2);
        Cv2.Circle(reference, new Point(184, 126), 14, Scalar.White, 2);

        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var profile = map.Recognition.FirstFloor;
        profile.RecognitionRegion = new NormalizedRectangle { Width = 1d, Height = 1d };
        profile.RecognitionPixelWidth = reference.Width;
        profile.RecognitionPixelHeight = reference.Height;
        profile.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.75d, Width = 0.05d, Height = 0.05d };
        profile.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.85d, Y = 0.15d, Width = 0.05d, Height = 0.05d };
        profile.Anchors.Add(new RecognitionAnchor
        {
            Key = "optional-box",
            DisplayName = "box",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d,
            Bounds = new NormalizedRectangle
            {
                X = 24d / reference.Width,
                Y = 30d / reference.Height,
                Width = 32d / reference.Width,
                Height = 30d / reference.Height
            }
        });
        profile.Anchors.Add(new RecognitionAnchor
        {
            Key = "optional-circle",
            DisplayName = "circle",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d,
            Bounds = new NormalizedRectangle
            {
                X = 168d / reference.Width,
                Y = 110d / reference.Height,
                Width = 32d / reference.Width,
                Height = 32d / reference.Height
            }
        });
        fingerprint = new MapGeometryFingerprint
        {
            Map = map,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height
        };
        return reference;
    }

    private static Mat BuildReference(out MapGeometryFingerprint fingerprint)
    {
        var reference = new Mat(new Size(240, 180), MatType.CV_8UC3, Scalar.All(0));
        Cv2.Rectangle(reference, new Rect(24, 30, 32, 30), Scalar.White, 3);
        Cv2.Line(reference, new Point(25, 58), new Point(54, 31), Scalar.White, 2);
        Cv2.Circle(reference, new Point(184, 126), 14, Scalar.White, 3);
        Cv2.Line(reference, new Point(174, 126), new Point(194, 126), Scalar.White, 2);

        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var profile = map.Recognition.FirstFloor;
        profile.RecognitionRegion = new NormalizedRectangle { Width = 1d, Height = 1d };
        profile.RecognitionPixelWidth = reference.Width;
        profile.RecognitionPixelHeight = reference.Height;
        profile.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.75d, Width = 0.05d, Height = 0.05d };
        profile.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.85d, Y = 0.15d, Width = 0.05d, Height = 0.05d };
        profile.Anchors.Add(new RecognitionAnchor
        {
            Key = "optional-box",
            DisplayName = "box",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d,
            Bounds = new NormalizedRectangle
            {
                X = 24d / reference.Width,
                Y = 30d / reference.Height,
                Width = 32d / reference.Width,
                Height = 30d / reference.Height
            }
        });
        profile.Anchors.Add(new RecognitionAnchor
        {
            Key = "optional-circle",
            DisplayName = "triangle",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d,
            Bounds = new NormalizedRectangle
            {
                X = 168d / reference.Width,
                Y = 110d / reference.Height,
                Width = 32d / reference.Width,
                Height = 32d / reference.Height
            }
        });
        fingerprint = new MapGeometryFingerprint
        {
            Map = map,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height
        };
        return reference;
    }

    private static MapOverlayTransform LockedTransform(Mat reference) => new()
    {
        ScaleX = 1d,
        ScaleY = 1d,
        ReferenceWidth = reference.Width,
        ReferenceHeight = reference.Height,
        AlignmentMode = MapOverlayAlignmentMode.IndependentAxes
    };
}
