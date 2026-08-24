using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace IdentityVisionBridge.PluginPackaging;

public sealed class IdvpPackageReader
{
    public async Task<IdvpValidatedPackage> ValidateAsync(
        string packagePath,
        string? extractionDirectory = null,
        IdvpValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new IdvpValidationOptions();
        packagePath = Path.GetFullPath(packagePath);
        await using var packageStream = new FileStream(
            packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (packageStream.Length <= IdvpConstants.HeaderSize ||
            packageStream.Length > IdvpConstants.MaxExpandedBytes + 32L * 1024 * 1024)
        {
            throw new IdvpPackageException("The IDVP package size is invalid.");
        }

        var header = IdvpHeader.Read(packageStream);
        await using var zipBytes = new MemoryStream(checked((int)(packageStream.Length - IdvpConstants.HeaderSize)));
        await packageStream.CopyToAsync(zipBytes, cancellationToken);
        zipBytes.Position = 0;

        using var archive = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 2 or > IdvpConstants.MaxEntries)
        {
            throw new IdvpPackageException("The IDVP archive has an invalid number of entries.");
        }

        var entries = IndexEntries(archive);
        var manifestBytes = await ReadJsonEntryAsync(entries, "manifest.json", cancellationToken);
        var signatureBytes = await ReadJsonEntryAsync(entries, "signature.json", cancellationToken);
        ValidateHeader(header, manifestBytes);

        IdvpManifest manifest;
        IdvpSignature signature;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, IdvpJsonContext.Default.IdvpManifest)
                ?? throw new IdvpPackageException("The IDVP manifest is empty.");
            signature = JsonSerializer.Deserialize(signatureBytes, IdvpJsonContext.Default.IdvpSignature)
                ?? throw new IdvpPackageException("The IDVP signature record is empty.");
        }
        catch (JsonException exception)
        {
            throw new IdvpPackageException("The IDVP manifest or signature JSON is invalid.", exception);
        }

        IdvpCrypto.Verify(manifestBytes, signature, options.AllowUnsigned);
        var isSigned = !string.Equals(signature.Algorithm, IdvpConstants.UnsignedAlgorithm, StringComparison.Ordinal);
        IdvpManifestValidator.Validate(manifest, isSigned);
        if (isSigned && !string.Equals(manifest.Publisher.KeyId, signature.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdvpPackageException("The publisher key in the manifest does not match the package signature.");
        }

        ValidatePayloadIndex(manifest, entries);
        await ValidatePayloadAsync(manifest, entries, cancellationToken);

        string? extractedDirectory = null;
        if (options.ExtractFiles)
        {
            if (string.IsNullOrWhiteSpace(extractionDirectory))
            {
                throw new ArgumentException("An extraction directory is required when ExtractFiles is true.", nameof(extractionDirectory));
            }

            extractedDirectory = Path.GetFullPath(extractionDirectory);
            await ExtractAsync(entries, extractedDirectory, cancellationToken);
        }

        return new IdvpValidatedPackage
        {
            Manifest = manifest,
            Signature = signature,
            PackagePath = packagePath,
            ExtractedDirectory = extractedDirectory
        };
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                throw new IdvpPackageException("Explicit directory entries are not allowed in IDVP v1 packages.");
            }

            var path = IdvpPathRules.ValidateArchivePath(entry.FullName);
            if (!entries.TryAdd(path, entry))
            {
                throw new IdvpPackageException($"The IDVP archive contains a duplicate or case-conflicting path: {path}");
            }

            if (IsLinkOrReparsePoint(entry))
            {
                throw new IdvpPackageException($"Links and reparse points are not allowed in IDVP packages: {path}");
            }

            if (entry.Length < 0 || entry.Length > IdvpConstants.MaxSingleFileBytes)
            {
                throw new IdvpPackageException($"The IDVP entry exceeds the single-file limit: {path}");
            }

            total = checked(total + entry.Length);
            if (total > IdvpConstants.MaxExpandedBytes + (2L * IdvpConstants.MaxJsonBytes))
            {
                throw new IdvpPackageException("The IDVP expanded-size limit was exceeded.");
            }
        }

        return entries;
    }

    private static async Task<byte[]> ReadJsonEntryAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path,
        CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(path, out var entry) || entry.Length > IdvpConstants.MaxJsonBytes)
        {
            throw new IdvpPackageException($"Required IDVP metadata entry is missing or too large: {path}");
        }

        await using var stream = entry.Open();
        using var buffer = new MemoryStream(checked((int)entry.Length));
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != entry.Length)
        {
            throw new IdvpPackageException($"Metadata entry length changed while reading: {path}");
        }

        return buffer.ToArray();
    }

    private static void ValidateHeader(IdvpHeader header, ReadOnlySpan<byte> manifestBytes)
    {
        if (header.ManifestLength != manifestBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(header.ManifestSha256, SHA256.HashData(manifestBytes)))
        {
            throw new IdvpPackageException("The IDVP header does not match manifest.json.");
        }
    }

    private static void ValidatePayloadIndex(
        IdvpManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        var expected = new HashSet<string>(manifest.Files.Select(static file => file.Path), StringComparer.OrdinalIgnoreCase)
        {
            "manifest.json",
            "signature.json"
        };

        var extra = entries.Keys.FirstOrDefault(path => !expected.Contains(path));
        if (extra is not null)
        {
            throw new IdvpPackageException($"The IDVP archive contains an undeclared file: {extra}");
        }

        var missing = expected.FirstOrDefault(path => !entries.ContainsKey(path));
        if (missing is not null)
        {
            throw new IdvpPackageException($"The IDVP archive is missing a declared file: {missing}");
        }

        if (!manifest.Files.Any(file => file.Path.Equals(manifest.EntryPoint.Assembly, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IdvpPackageException("The configured entry assembly is not declared in the file list.");
        }

        var depsPath = Path.ChangeExtension(manifest.EntryPoint.Assembly, ".deps.json").Replace('\\', '/');
        if (!manifest.Files.Any(file => file.Path.Equals(depsPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IdvpPackageException("The entry assembly .deps.json file is required.");
        }

        foreach (var file in manifest.Files)
        {
            var name = Path.GetFileName(file.Path);
            if (name.Equals("IdentityVisionBridge.PluginSdk.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("IDVB.PluginContracts.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("IDVB.PluginHostMessages.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("IDVBuff.dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new IdvpPackageException($"The package carries a host-shared assembly: {file.Path}");
            }
        }
    }

    private static async Task ValidatePayloadAsync(
        IdvpManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files)
        {
            var entry = entries[file.Path];
            if (entry.Length != file.Length)
            {
                throw new IdvpPackageException($"File length does not match the manifest: {file.Path}");
            }

            await using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            int read;
            long length = 0;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                length = checked(length + read);
                if (length > file.Length)
                {
                    throw new IdvpPackageException($"File expanded beyond its declared length: {file.Path}");
                }

                hash.AppendData(buffer, 0, read);
            }

            var actualHash = hash.GetHashAndReset();
            if (length != file.Length ||
                !CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(file.Sha256)))
            {
                throw new IdvpPackageException($"File hash does not match the manifest: {file.Path}");
            }

            if (file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                await using var assemblyStream = entry.Open();
                if (file.Path.Equals(manifest.EntryPoint.Assembly, StringComparison.OrdinalIgnoreCase))
                {
                    IdvpAssemblyInspector.InspectEntryAssembly(assemblyStream, manifest.EntryPoint.Type);
                }
                else
                {
                    IdvpAssemblyInspector.InspectManagedDependency(assemblyStream, file.Path);
                }
            }
        }
    }

    private static async Task ExtractAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string extractionDirectory,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(extractionDirectory) && Directory.EnumerateFileSystemEntries(extractionDirectory).Any())
        {
            throw new IdvpPackageException("The IDVP extraction directory must be empty.");
        }

        Directory.CreateDirectory(extractionDirectory);
        foreach (var (path, entry) in entries)
        {
            var destination = IdvpPathRules.ResolveExtractionPath(extractionDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = entry.ExternalAttributes & 0xFFFF;
        return unixMode == 0xA000 || (windowsAttributes & 0x0400) != 0;
    }
}
