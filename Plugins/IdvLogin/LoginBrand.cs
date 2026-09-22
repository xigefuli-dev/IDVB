namespace IDVBuff.Plugins.IdvLogin;

public sealed record LoginBrand(string Name, uint Color)
{
    public static LoginBrand For(string channel) => channel switch
    {
        "huawei" => new("华为", 0xFFFF5C62),
        "4399" or "4399com" => new("4399", 0xFF70C447),
        "wechat" => new("微信", 0xFF50C878),
        "myapp" => new("腾讯", 0xFF609CFF),
        "qq" => new("QQ", 0xFF609CFF),
        "bilibili_sdk" => new("哔哩哔哩", 0xFFFB7299),
        "xiaomi" or "xiaomi_app" => new("小米", 0xFFFF8534),
        "oppo" => new("OPPO", 0xFF50B879),
        "vivo" or "nearme_vivo" => new("vivo", 0xFF609CFF),
        "honor_sdk" => new("荣耀", 0xFF609CFF),
        "uc_platform" => new("九游", 0xFFFF8534),
        "360_assistant" => new("360", 0xFF70C447),
        "netease" => new("网易", 0xFFFF6464),
        _ => new(string.IsNullOrWhiteSpace(channel) ? "未知来源" : channel, 0xFFAAAAAA)
    };
}
