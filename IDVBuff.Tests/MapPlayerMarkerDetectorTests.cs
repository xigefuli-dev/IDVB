using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapPlayerMarkerDetectorTests
{
    [Fact]
    public void CalibratedMarkerIsLocatedInViewportAndScreenCoordinates()
    {
        using var template = new Mat(
            new Size(20, 20),
            MatType.CV_8UC3,
            new Scalar(18, 18, 18));
        Cv2.Circle(
            template,
            new Point(10, 10),
            7,
            new Scalar(40, 220, 255),
            -1);
        Cv2.Line(
            template,
            new Point(10, 2),
            new Point(10, 18),
            Scalar.White,
            2);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"idvbuff-player-{Guid.NewGuid():N}.png");
        Cv2.ImWrite(path, template);
        try
        {
            using var live = new Mat(
                new Size(300, 220),
                MatType.CV_8UC3,
                new Scalar(18, 18, 18));
            using (var destination = new Mat(
                live,
                new Rect(120, 80, template.Width, template.Height)))
            {
                template.CopyTo(destination);
            }
            using var detector = new MapPlayerMarkerDetector();

            var result = detector.Detect(
                live,
                new MapScreenRect(1000, 500, 300, 220),
                new MapScreenRect(900, 400, 800, 600),
                PlayerSlot.Player1,
                path,
                previousPoint: null);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.InRange(result.Confidence, 0.70d, 1d);
            Assert.InRange(result.ViewportPoint.X, 129d, 131d);
            Assert.InRange(result.ViewportPoint.Y, 89d, 91d);
            Assert.InRange(result.ScreenPoint.X, 1129d, 1131d);
            Assert.InRange(result.ScreenPoint.Y, 589d, 591d);
            Assert.InRange(result.ShapeAgreement, 0d, 1d);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(PlayerSlot.Player1, 0.8)]
    [InlineData(PlayerSlot.Player2, 1.0)]
    [InlineData(PlayerSlot.Player3, 1.2)]
    [InlineData(PlayerSlot.Player4, 1.5)]
    public void PackagedPlayerMarkerIsRecognizedAcrossSupportedScales(
        PlayerSlot playerSlot,
        double scale)
    {
        var path = MapPlayerAssetCatalog.ResolvePath(playerSlot);
        using var template = Cv2.ImRead(path, ImreadModes.Color);
        using var scaled = new Mat();
        Cv2.Resize(
            template,
            scaled,
            new Size(
                (int)Math.Round(template.Width * scale),
                (int)Math.Round(template.Height * scale)));
        using var live = new Mat(
            new Size(420, 280),
            MatType.CV_8UC3,
            new Scalar(8, 12, 18));
        var target = new Rect(170, 105, scaled.Width, scaled.Height);
        using (var destination = new Mat(live, target))
            scaled.CopyTo(destination);
        using var detector = new MapPlayerMarkerDetector();

        var result = detector.Detect(
            live,
            new MapScreenRect(600, 300, live.Width, live.Height),
            new MapScreenRect(600, 300, live.Width, live.Height),
            playerSlot,
            path,
            previousPoint: null);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(playerSlot, result.PlayerSlot);
        Assert.InRange(
            result.ViewportPoint.X,
            target.X + (target.Width / 2d) - 2d,
            target.X + (target.Width / 2d) + 2d);
        Assert.InRange(
            result.ViewportPoint.Y,
            target.Y + (target.Height / 2d) - 2d,
            target.Y + (target.Height / 2d) + 2d);
    }

    [Fact]
    public void DetectorDoesNotAcceptAnotherPlayerSlot()
    {
        var expectedPath =
            MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player1);
        var otherPath =
            MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player2);
        using var other = Cv2.ImRead(otherPath, ImreadModes.Color);
        using var live = new Mat(
            new Size(320, 220),
            MatType.CV_8UC3,
            new Scalar(8, 12, 18));
        using (var destination = new Mat(
            live,
            new Rect(120, 80, other.Width, other.Height)))
        {
            other.CopyTo(destination);
        }
        using var detector = new MapPlayerMarkerDetector();

        var result = detector.Detect(
            live,
            new MapScreenRect(0, 0, live.Width, live.Height),
            new MapScreenRect(0, 0, live.Width, live.Height),
            PlayerSlot.Player1,
            expectedPath,
            previousPoint: null);

        Assert.False(
            result.Succeeded,
            $"score={result.TemplateScore:F3}, color={result.ColorAgreement:F3}, shape={result.ShapeAgreement:F3}, confidence={result.Confidence:F3}");
    }

    [Fact]
    public void LocalTrackingFallsBackToGlobalSearchAfterFiveFailures()
    {
        var slot = PlayerSlot.Player4;
        var path = MapPlayerAssetCatalog.ResolvePath(slot);
        using var template = Cv2.ImRead(path, ImreadModes.Color);
        using var live = new Mat(
            new Size(640, 360),
            MatType.CV_8UC3,
            new Scalar(8, 12, 18));
        var target = new Rect(
            520,
            260,
            template.Width,
            template.Height);
        using (var destination = new Mat(live, target))
            template.CopyTo(destination);
        using var detector = new MapPlayerMarkerDetector();
        var previous = new MapViewportPoint(60, 60);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var local = detector.Detect(
                live,
                new MapScreenRect(0, 0, live.Width, live.Height),
                new MapScreenRect(0, 0, live.Width, live.Height),
                slot,
                path,
                previous);
            Assert.False(local.Succeeded);
        }

        var recovered = detector.Detect(
            live,
            new MapScreenRect(0, 0, live.Width, live.Height),
            new MapScreenRect(0, 0, live.Width, live.Height),
            slot,
            path,
            previous);

        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.InRange(recovered.ViewportPoint.X, 541d, 544d);
        Assert.InRange(recovered.ViewportPoint.Y, 282d, 285d);
    }
}
