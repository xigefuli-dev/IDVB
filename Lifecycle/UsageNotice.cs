using System.Security.Cryptography;
using System.Text;

namespace IDVBuff.Lifecycle;

/// <summary>Content-based acknowledgement, independent of release and installation directories.</summary>
internal static class UsageNotice
{
    internal const string Title = "软件性质及使用责任声明";
    internal const string Confirmation = "我已阅读并确认";
    internal const string Text = "Identity Vision Bridge（IDVB）是独立的辅助工具，不属于游戏外挂。本软件不修改游戏数据，不读取或修改游戏进程内存，不通过上述方式干预游戏运行。\n\n"
        + "用户应自行遵守游戏运营方的用户协议、管理规则及相关规定。严禁以本软件为由实施、掩饰或为任何违规行为辩解。\n\n"
        + "因用户自身行为，包括但不限于使用外挂或其他违规工具、辱骂或骚扰他人、违反游戏规则等，所产生的账号警告、功能限制、停权、封禁及其他损失或争议，由用户自行承担相应责任。本软件及其开发者不为用户自身违规行为承担责任。\n\n"
        + "本声明不代表游戏运营方的认可或授权，亦不构成账号不会受到限制或处罚的保证。依法应由相关责任主体承担的责任，不因本声明而免除。\n\n"
        + "请完整阅读本声明。等待 5 秒并明确确认后，方可继续使用；未确认时退出，下一次启动仍须确认。";

    internal static string Fingerprint { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Title + "\n" + Text + "\n" + Confirmation)));
    internal static string ReceiptPath => Path.Combine(AppDataPaths.RootDirectory, "usage-notice-acknowledgement.txt");

    internal static bool IsAccepted(string? path = null)
    {
        try { return File.ReadAllText(path ?? ReceiptPath, Encoding.UTF8) == Fingerprint; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void Accept(string? path = null)
    {
        path ??= ReceiptPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A partial write can only trigger a new confirmation, never grant access.
        File.WriteAllText(path, Fingerprint, Encoding.UTF8);
        if (!IsAccepted(path)) throw new IOException("无法验证声明确认记录。");
    }
}
