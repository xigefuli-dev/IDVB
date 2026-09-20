using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IDVBuff.Survey.Idvm;

internal static class SurveyIdvmSecurity
{
    public const int HeaderSize = 80;
    public const int MaximumEntries = 10000;
    public const long MaximumSingleAssetBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumManifestBytes = 16L * 1024 * 1024;

    public static byte[] CreateHeader(
        Guid packageId,
        DateTimeOffset createdAt,
        ReadOnlySpan<byte> manifestHash,
        ushort minorVersion = 2)
    {
        var bytes = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("IDVM").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), minorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), HeaderSize);
        Convert.FromHexString(packageId.ToString("N")).CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28, 8), createdAt.ToUnixTimeMilliseconds());
        manifestHash.CopyTo(bytes.AsSpan(36, 32));
        return bytes;
    }

    public static void ValidateHeader(
        ReadOnlySpan<byte> bytes,
        Guid packageId,
        DateTimeOffset createdAt,
        ReadOnlySpan<byte> manifestHash)
    {
        if (bytes.Length != HeaderSize || !bytes[..4].SequenceEqual("IDVM"u8))
            throw new InvalidDataException("IDVM header 无效。");
        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        if (major != 1 || minor is not (1 or 2))
            throw new NotSupportedException($"当前读取器不支持 IDVM {major}.{minor} 测绘项目。");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2)) != HeaderSize
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(10, 2)) != 0
            || bytes.Slice(68, 12).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("IDVM 1.1 header 的长度、标志或保留字段无效。");
        var headerId = Guid.ParseExact(Convert.ToHexString(bytes.Slice(12, 16)), "N");
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(28, 8));
        if (headerId != packageId || timestamp != createdAt.ToUnixTimeMilliseconds())
            throw new InvalidDataException("IDVM header 与 manifest 标识不一致。");
        if (!CryptographicOperations.FixedTimeEquals(bytes.Slice(36, 32), manifestHash))
            throw new InvalidDataException("IDVM manifest 摘要与 header 不一致。");
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 512
            || path.Contains('\\')
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"IDVM 包含不安全路径：{path}");
    }

    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
