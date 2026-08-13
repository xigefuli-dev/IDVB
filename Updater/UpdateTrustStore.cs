using IDVBuff.UpdateCore;

namespace IDVBuff.Updater;

internal static class UpdateTrustStore
{
    public static EcdsaUpdateFeedVerifier CreateVerifier()
    {
        var trustDirectory = Path.Combine(AppContext.BaseDirectory, "UpdateTrust");
        var keys = Directory.Exists(trustDirectory)
            ? Directory.EnumerateFiles(trustDirectory, "*.pem", SearchOption.TopDirectoryOnly)
                .ToDictionary(
                    path => Path.GetFileNameWithoutExtension(path),
                    File.ReadAllText,
                    StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.Count == 0)
            throw new UpdateTrustException("更新器没有可用的 IDVB 更新公钥。");
        return new EcdsaUpdateFeedVerifier(keys);
    }
}
