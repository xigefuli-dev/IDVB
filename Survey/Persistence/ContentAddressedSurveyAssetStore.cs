using System.Security.Cryptography;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed class ContentAddressedSurveyAssetStore : ISurveyAssetStore
{
    private readonly SurveyStoragePaths _paths;

    public ContentAddressedSurveyAssetStore(SurveyStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<SurveyAssetReference> PutAsync(
        Guid projectId,
        SurveyEncodedFrame frame,
        CancellationToken cancellationToken = default)
    {
        if (frame.Bytes.IsEmpty)
            throw new ArgumentException("Survey frame is empty.", nameof(frame));
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            throw new ArgumentException("Survey frame dimensions are invalid.", nameof(frame));

        using var source = new MemoryStream(frame.Bytes.ToArray(), writable: false);
        return await PutStreamAsync(
            projectId,
            source,
            frame.FileExtension,
            frame.MediaType,
            frame.PixelWidth,
            frame.PixelHeight,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyAssetReference> PutStreamAsync(
        Guid projectId,
        Stream source,
        string fileExtension,
        string mediaType,
        int pixelWidth,
        int pixelHeight,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Survey asset source must be readable.", nameof(source));
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentException("Survey asset dimensions are invalid.", nameof(pixelWidth));
        var extension = NormalizeExtension(fileExtension);
        var temporaryDirectory = _paths.TemporaryDirectory(projectId);
        Directory.CreateDirectory(temporaryDirectory);
        var temporary = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}{extension}.tmp");
        string digest;
        long byteLength = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    byteLength = checked(byteLength + read);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (byteLength == 0)
                throw new InvalidDataException("Survey asset source is empty.");
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (expectedSha256 is not null
                && !string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Survey asset digest verification failed.");
            var relativePath = Path.Combine("assets", digest[..2], digest + extension);
            var destination = _paths.ResolveProjectRelativePath(projectId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }
            return new SurveyAssetReference(
                digest,
                relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                mediaType,
                byteLength,
                pixelWidth,
                pixelHeight);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(
        Guid projectId,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveAbsolutePath(projectId, asset);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public string ResolveAbsolutePath(Guid projectId, SurveyAssetReference asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return _paths.ResolveProjectRelativePath(projectId, asset.RelativePath);
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;
        if (normalized.Length > 10 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
            throw new ArgumentException("Survey asset extension is invalid.", nameof(extension));
        return normalized.ToLowerInvariant();
    }
}
