using System.Security.Cryptography;

namespace IDVBuff.Views;

internal enum SponsorshipAsset { WeChat, Alipay, Banner }

internal static class SponsorshipAssets
{
    // These pins belong to the author's original bytes. Do not regenerate them at build time:
    // doing so would silently approve substituted artwork. A patched executable can bypass
    // any local check; this protects the original app against resource/file substitution.
    private static (string Name, string Hash) Identity(SponsorshipAsset asset) => asset switch
    {
        SponsorshipAsset.WeChat => ("wechat.png", "F8AE2F97A65B692C7B39ABDA9141784EAEE5F26A2892212E71E3A53440998883"),
        SponsorshipAsset.Alipay => ("alipay.png", "4AB013527FDC3ECFC6A1CCB01467098151637CBEAD37FC42651E9F09E7ACB862"),
        SponsorshipAsset.Banner => ("banner.png", "9B8AE9B04E03FA5C7004C19C44A57B5CD1E55864BE4BE0D0D95366F46892C5CC"),
        _ => throw new ArgumentOutOfRangeException(nameof(asset))
    };

    internal static byte[] ReadVerified(SponsorshipAsset asset)
    {
        var (name, _) = Identity(asset);
        using var resource = typeof(SponsorshipAssets).Assembly.GetManifestResourceStream("IDVB.Sponsorship." + name)
            ?? throw new InvalidDataException("赞助资源缺失。");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        Verify(asset, bytes);
        return bytes;
    }

    internal static void Verify(SponsorshipAsset asset, ReadOnlySpan<byte> bytes)
    {
        var (_, hash) = Identity(asset);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash)))
            throw new InvalidDataException("赞助资源完整性校验失败。");
    }
}
