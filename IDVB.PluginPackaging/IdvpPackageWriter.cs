using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace IdentityVisionBridge.PluginPackaging;

public sealed class IdvpPackageWriter
{
    private static readonly DateTimeOffset DeterministicZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<IdvpValidatedPackage> PackAsync(
        IdvpPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sourceDirectory = Path.GetFullPath(options.SourceDirectory);
        var manifestPath = Path.GetFullPath(options.ManifestPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        if (!outputPath.EndsWith(".idvp", StringComparison.OrdinalIgnoreCase))
        {
            throw new IdvpPackageException("IDVP package output must use the .idvp extension.");
        }

        if (!Directory.Exists(sourceDirectory) || !File.Exists(manifestPath))
        {
            throw new IdvpPackageException("The plugin source directory or manifest template does not exist.");
        }

        IdvpManifest template;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            template = await JsonSerializer.DeserializeAsync(
                    manifestStream, IdvpJsonContext.Default.IdvpManifest, cancellationToken)
                ?? throw new IdvpPackageException("The manifest template is empty.");
        }
        catch (JsonException exception)
        {
            throw new IdvpPackageException("The manifest template contains invalid JSON.", exception);
        }

        var privateKeyPem = options.PrivateKeyPemPath is null
            ? null
            : await File.ReadAllTextAsync(Path.GetFullPath(options.PrivateKeyPemPath), cancellationToken);
        var keyId = privateKeyPem is null ? null : IdvpCrypto.GetKeyIdFromPrivateKeyPem(privateKeyPem);
        var files = await BuildFileListAsync(sourceDirectory, manifestPath, outputPath, cancellationToken);
        var manifest = template with
        {
            Publisher = template.Publisher with { KeyId = keyId },
            Files = files
        };

        IdvpManifestValidator.Validate(manifest, privateKeyPem is not null);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, IdvpJsonContext.Default.IdvpManifest);
        var signature = privateKeyPem is null
            ? new IdvpSignature { Algorithm = IdvpConstants.UnsignedAlgorithm }
            : IdvpCrypto.Sign(manifestBytes, privateKeyPem);
        var signatureBytes = JsonSerializer.SerializeToUtf8Bytes(signature, IdvpJsonContext.Default.IdvpSignature);

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var zipPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{operationId}.zip.tmp");
        var packageTempPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{operationId}.tmp");
        try
        {
            await CreateZipAsync(
                zipPath, sourceDirectory, manifestBytes, signatureBytes, files, cancellationToken);
            await WritePackageAsync(packageTempPath, zipPath, manifestBytes, cancellationToken);

            var reader = new IdvpPackageReader();
            var validated = await reader.ValidateAsync(
                packageTempPath,
                options: new IdvpValidationOptions { AllowUnsigned = privateKeyPem is null, ExtractFiles = false },
                cancellationToken: cancellationToken);

            File.Move(packageTempPath, outputPath, overwrite: true);
            return validated with { PackagePath = outputPath };
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteFile(packageTempPath);
        }
    }

    public async Task<IdvpValidatedPackage> SignAsync(
        string inputPath,
        string outputPath,
        string privateKeyPemPath,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "idvb-plugin-sign", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var reader = new IdvpPackageReader();
            var package = await reader.ValidateAsync(
                inputPath,
                tempDirectory,
                new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = true },
                cancellationToken);
            if (package.IsSigned)
            {
                throw new IdvpPackageException("The sign command only accepts unsigned IDVP packages.");
            }

            return await PackAsync(
                new IdvpPackOptions
                {
                    SourceDirectory = tempDirectory,
                    ManifestPath = Path.Combine(tempDirectory, "manifest.json"),
                    OutputPath = outputPath,
                    PrivateKeyPemPath = privateKeyPemPath
                },
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<IdvpFileEntry>> BuildFileListAsync(
        string sourceDirectory,
        string manifestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var results = new List<IdvpFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(filePath);
            if (fullPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDirectory, fullPath).Replace('\\', '/');
            relativePath = IdvpPathRules.ValidateArchivePath(relativePath);
            if (relativePath is "manifest.json" or "signature.json")
            {
                continue;
            }

            if (!seen.Add(relativePath))
            {
                throw new IdvpPackageException($"Source files contain a case-conflicting path: {relativePath}");
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IdvpPackageException($"Source links and reparse points are not allowed: {relativePath}");
            }

            var info = new FileInfo(fullPath);
            if (info.Length > IdvpConstants.MaxSingleFileBytes)
            {
                throw new IdvpPackageException($"Source file exceeds the IDVP limit: {relativePath}");
            }

            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            results.Add(new IdvpFileEntry
            {
                Path = relativePath,
                Length = info.Length,
                Sha256 = Convert.ToHexStringLower(hash)
            });
        }

        return results.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task CreateZipAsync(
        string zipPath,
        string sourceDirectory,
        ReadOnlyMemory<byte> manifestBytes,
        ReadOnlyMemory<byte> signatureBytes,
        IReadOnlyList<IdvpFileEntry> files,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(archive, "manifest.json", manifestBytes, cancellationToken);
        await WriteEntryAsync(archive, "signature.json", signatureBytes, cancellationToken);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
            entry.LastWriteTime = DeterministicZipTimestamp;
            await using var entryStream = entry.Open();
            await using var input = new FileStream(
                Path.Combine(sourceDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            await input.CopyToAsync(entryStream, cancellationToken);
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicZipTimestamp;
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WritePackageAsync(
        string packagePath,
        string zipPath,
        ReadOnlyMemory<byte> manifestBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        await output.WriteAsync(IdvpHeader.Create(manifestBytes.Span), cancellationToken);
        await using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await zip.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
