using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IdentityVisionBridge.PluginPackaging;

internal sealed record IdvpHeader(long ManifestLength, byte[] ManifestSha256)
{
    public static IdvpHeader Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[IdvpConstants.HeaderSize];
        stream.ReadExactly(header);

        if (!header[..IdvpConstants.Magic.Length].SequenceEqual(IdvpConstants.Magic))
        {
            throw new IdvpPackageException("The file does not have an IDVP header.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        var manifestLength = BinaryPrimitives.ReadInt64LittleEndian(header[16..24]);
        if (version != IdvpConstants.FormatVersion || size != IdvpConstants.HeaderSize)
        {
            throw new IdvpPackageException($"Unsupported IDVP header version {version}.");
        }

        if (manifestLength is <= 0 or > IdvpConstants.MaxJsonBytes)
        {
            throw new IdvpPackageException("The manifest length in the IDVP header is invalid.");
        }

        return new IdvpHeader(manifestLength, header[24..56].ToArray());
    }

    public static byte[] Create(ReadOnlySpan<byte> manifestBytes)
    {
        var header = new byte[IdvpConstants.HeaderSize];
        IdvpConstants.Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), IdvpConstants.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), IdvpConstants.HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), manifestBytes.Length);
        SHA256.HashData(manifestBytes).CopyTo(header.AsSpan(24, 32));
        return header;
    }
}
