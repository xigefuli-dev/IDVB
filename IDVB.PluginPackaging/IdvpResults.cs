namespace IdentityVisionBridge.PluginPackaging;

public sealed record IdvpValidationOptions
{
    public bool AllowUnsigned { get; init; }

    public bool ExtractFiles { get; init; } = true;
}

public sealed record IdvpValidatedPackage
{
    public required IdvpManifest Manifest { get; init; }

    public required IdvpSignature Signature { get; init; }

    public required string PackagePath { get; init; }

    public string? ExtractedDirectory { get; init; }

    public bool IsSigned => string.Equals(Signature.Algorithm, IdvpConstants.SignatureAlgorithm, StringComparison.Ordinal);
}

public sealed record IdvpPackOptions
{
    public required string SourceDirectory { get; init; }

    public required string ManifestPath { get; init; }

    public required string OutputPath { get; init; }

    public string? PrivateKeyPemPath { get; init; }
}

public sealed class IdvpPackageException : Exception
{
    public IdvpPackageException(string message)
        : base(message)
    {
    }

    public IdvpPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class IdvpConstants
{
    public const int FormatVersion = 1;
    public const int HeaderSize = 80;
    public const int MaxEntries = 4096;
    public const long MaxSingleFileBytes = 256L * 1024 * 1024;
    public const long MaxExpandedBytes = 512L * 1024 * 1024;
    public const int MaxJsonBytes = 8 * 1024 * 1024;
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256";
    public const string UnsignedAlgorithm = "none";

    public static ReadOnlySpan<byte> Magic => "IDVP\r\n\u001a\n"u8;
}
