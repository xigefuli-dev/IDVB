using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace IDVBuff.Features.GameLaunch;

public sealed partial class FeverAccountStore
{
    private const string FeverDataRegistryPath = @"Software\FeverGames\FeverGamesInstaller\data";
    private const string FeverSettingRegistryPath = @"Software\FeverGames\FeverGamesInstaller\setting";

    /// <summary>
    /// Reads the current logged-in NetEase Fever credentials from the Windows Registry.
    /// </summary>
    public static (string? Token, string? Sdkuid) ReadFeverRegistryCredentials()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(FeverDataRegistryPath, writable: false);
            if (key is null) return (null, null);

            var token = key.GetValue("token") as string;
            var sdkuid = key.GetValue("sdkuid") as string;
            return (token, sdkuid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] ReadFeverRegistryCredentials failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Deploys the specified NetEase Fever credentials to the Windows Registry.
    /// </summary>
    public static bool WriteFeverRegistryCredentials(string? token, string? sdkuid)
    {
        try
        {
            using var dataKey = Registry.CurrentUser.CreateSubKey(FeverDataRegistryPath, writable: true);
            if (dataKey is null) return false;

            if (!string.IsNullOrEmpty(token))
                dataKey.SetValue("token", token, RegistryValueKind.String);
            else
                dataKey.DeleteValue("token", throwOnMissingValue: false);

            if (!string.IsNullOrEmpty(sdkuid))
                dataKey.SetValue("sdkuid", sdkuid, RegistryValueKind.String);
            else
                dataKey.DeleteValue("sdkuid", throwOnMissingValue: false);

            using var settingKey = Registry.CurrentUser.CreateSubKey(FeverSettingRegistryPath, writable: true);
            settingKey?.SetValue("autoLogin", "true", RegistryValueKind.String);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] WriteFeverRegistryCredentials failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the NetEase Fever credentials in the registry before a fresh login.
    /// </summary>
    public static void ClearFeverRegistryCredentials()
    {
        try
        {
            using var dataKey = Registry.CurrentUser.OpenSubKey(FeverDataRegistryPath, writable: true);
            dataKey?.DeleteValue("token", throwOnMissingValue: false);
            dataKey?.DeleteValue("sdkuid", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] ClearFeverRegistryCredentials failed: {ex.Message}");
        }
    }

    public sealed record ParsedFeverToken(string? DisplayName, string? Identifier, string? UserId, string? Token = null);

    /// <summary>
    /// Parses Fever's @ByteArray(...) format token into account identity fields.
    /// </summary>
    public static ParsedFeverToken TryParseFeverToken(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return new ParsedFeverToken(null, null, null, null);

        var trimmed = rawToken.Trim();
        string base64Payload;
        if (trimmed.StartsWith("@ByteArray(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(")"))
        {
            base64Payload = trimmed[11..^1].Trim();
        }
        else
        {
            base64Payload = trimmed;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(base64Payload);
            var json = Encoding.UTF8.GetString(jsonBytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            string? accName = root.TryGetProperty("account_name", out var accNameProp) ? accNameProp.GetString() : null;
            string? accId = root.TryGetProperty("account_id", out var accIdProp) ? accIdProp.GetString() : null;
            string? userId = root.TryGetProperty("userId", out var userProp) ? userProp.GetString() : null;
            string? token = root.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() : null;

            var displayName = !string.IsNullOrWhiteSpace(name) ? name :
                              !string.IsNullOrWhiteSpace(accName) ? accName :
                              !string.IsNullOrWhiteSpace(userId) ? userId : "网易账号";

            var identifier = !string.IsNullOrWhiteSpace(accName) ? accName :
                             !string.IsNullOrWhiteSpace(accId) ? accId :
                             !string.IsNullOrWhiteSpace(userId) ? userId : null;

            return new ParsedFeverToken(displayName, identifier, userId, token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] TryParseFeverToken failed: {ex.Message}");
            return new ParsedFeverToken(null, null, null, null);
        }
    }
}
