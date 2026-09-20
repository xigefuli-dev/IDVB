using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3CertificationCaptureTests
{
    [Fact]
    public void EvidenceOwnsPixelsAndReferenceAndDoesNotInventGroundTruth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idvb-certification-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var pixels = new Mat(32, 48, MatType.CV_8UC3, Scalar.Black);
        var reference = Path.Combine(directory, "reference.png");
        Assert.True(Cv2.ImWrite(reference, pixels));
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reference)));
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UnixEpoch, "gen");
        var result = Vpsg3BootstrapResult.Fallback("test", default, default);
        var path = Vpsg3CertificationCapture.Save(directory, pixels, reference, sha, key,
            new(1, 2, 48, 32), DateTimeOffset.UtcNow, result, null);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.True(root.GetProperty("fastAcceptLocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("groundTruth").ValueKind);
        Assert.False(root.GetProperty("baselineIsGroundTruth").GetBoolean());
        Assert.Equal(sha.ToLowerInvariant(), root.GetProperty("referenceSha256").GetString());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "live.png")));
        var before = Directory.GetFiles(directory, "sample.json", SearchOption.AllDirectories).Length;
        Assert.Throws<InvalidDataException>(() => Vpsg3CertificationCapture.Save(directory, pixels,
            reference, new string('0', 64), key, new(1, 2, 48, 32), DateTimeOffset.UtcNow, result, null));
        Assert.Equal(before, Directory.GetFiles(directory, "sample.json", SearchOption.AllDirectories).Length);
    }
}
