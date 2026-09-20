using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Opt-in, local replay evidence. A completed manifest is written last.</summary>
internal static class Vpsg3CertificationCapture
{
    internal const string EnableFileName = "vpsg3-certification.enabled";

    internal static string Save(string logDirectory, Mat pixels, string referencePath,
        string expectedReferenceSha256, Vpsg3IndexCacheKey key, MapScreenRect bounds,
        DateTimeOffset capturedAt, Vpsg3BootstrapResult result, MapOverlayTransform? baseline)
    {
        var reference = File.ReadAllBytes(referencePath);
        var referenceHash = Convert.ToHexString(SHA256.HashData(reference)).ToLowerInvariant();
        if (!string.Equals(referenceHash, expectedReferenceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reference changed since index preparation; certification sample not committed.");

        var root = Path.Combine(logDirectory, "Vpsg3Certification");
        var references = Path.Combine(root, "references");
        Directory.CreateDirectory(references);
        var savedReference = Path.Combine(references, referenceHash + ".png");
        if (!File.Exists(savedReference))
        {
            using var output = new FileStream(savedReference, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(reference);
        }
        else if (!SHA256.HashData(File.ReadAllBytes(savedReference)).AsSpan().SequenceEqual(SHA256.HashData(reference)))
            throw new InvalidDataException("Existing certification reference has a different hash.");

        var sampleId = $"{capturedAt:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var directory = Path.Combine(root, "samples", sampleId);
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "live.png");
        if (!Cv2.ImWrite(imagePath, pixels)) throw new IOException("Could not write certification frame.");
        var manifestPath = Path.Combine(directory, "sample.json");
        var payload = new
        {
            schemaVersion = 1, sampleId, capturedAt, source = "live-shadow",
            phase = 5, fastAcceptLocked = true, groundTruth = (object?)null,
            key, bounds, referenceSha256 = referenceHash,
            referencePath = Path.GetRelativePath(directory, savedReference),
            livePath = "live.png", liveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))).ToLowerInvariant(),
            solverModuleId = typeof(Vpsg3FastBootstrapSolver).Module.ModuleVersionId,
            solverVersion = typeof(Vpsg3FastBootstrapSolver).Assembly.GetName().Version?.ToString(),
            tuning = Vpsg3TuningConfig.Default, result, baseline, baselineIsGroundTruth = false
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return manifestPath;
    }
}
