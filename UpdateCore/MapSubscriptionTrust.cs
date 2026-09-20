namespace IDVBuff.UpdateCore;

public static class MapSubscriptionTrust
{
    public static string LoadOfficialPublicKey(string applicationBaseDirectory)
    {
        var path = Path.Combine(
            applicationBaseDirectory,
            "MapTrust",
            MapSubscriptionProtocol.OfficialTrustFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("缺少 IDVB 官方地图发布信任根。", path);
        return File.ReadAllText(path);
    }
}
