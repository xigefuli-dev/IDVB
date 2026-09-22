using System.Text.Json;

namespace IDVBuff.Plugins.IdvLogin;

public enum LoginSelectionResult { WaitingForGame, Submitted, NotCompleted }

internal static class LoginSelectionResponse
{
    public static LoginSelectionResult Parse(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return LoginSelectionResult.Submitted;
        if (value.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            return LoginSelectionResult.NotCompleted;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (value.TryGetProperty("error", out _) ||
            (value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False))
            return LoginSelectionResult.NotCompleted;
        if (value.TryGetProperty("code", out var code))
        {
            if (!code.TryGetInt32(out var number)) throw new JsonException();
            return number == 0 ? LoginSelectionResult.Submitted : LoginSelectionResult.NotCompleted;
        }
        // Without a game QR, upstream returns UniSDK data through the Kinich branch.
        // Classify it, then discard it. Never retain or log account credentials.
        if (HasText(value, "user_id") && HasText(value, "token") && HasText(value, "login_channel"))
            return LoginSelectionResult.WaitingForGame;
        // simulate_confirm forwards the successful HTTP 200 response, including {}.
        if (!value.EnumerateObject().Any()) return LoginSelectionResult.Submitted;
        throw new JsonException("无法确认 idv-login 登录结果。");
    }

    private static bool HasText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString());
}
