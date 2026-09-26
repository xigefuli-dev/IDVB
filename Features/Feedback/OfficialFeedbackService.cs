using System.Net.Http.Headers;
using System.Text.Json;

namespace IDVBuff.Features.Feedback;

/// <summary>
/// 官网后台反馈上传服务。
/// </summary>
public sealed class OfficialFeedbackService : IFeedbackService
{
    public static OfficialFeedbackService Instance { get; } = new();

    /// <summary>
    /// 提供当前有效发布 Token 的委托（桌面端启动时注入）。
    /// </summary>
    public static Func<string?>? TokenProvider { get; set; }

    /// <summary>
    /// 提供当前客户端版本的委托（桌面端启动时注入）。
    /// </summary>
    public static Func<string>? ClientVersionProvider { get; set; }

    /// <summary>
    /// 自定义测试 HttpClient 或 HttpMessageHandler 注入。
    /// </summary>
    public static HttpClient? CustomHttpClient { get; set; }

    private static readonly HttpClient DefaultHttpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    {
        BaseAddress = new Uri("https://community.idvb.xgflee.com/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static HttpClient Client => CustomHttpClient ?? DefaultHttpClient;

    /// <summary>
    /// 提交用户反馈。
    /// </summary>
    public async Task<FeedbackSubmissionResult> SubmitFeedbackAsync(
        FeedbackSubmissionPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var token = TokenProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(token))
        {
            return FeedbackSubmissionResult.Fail("反馈功能必须登录后才能使用，请先登录社区账户。");
        }

        FileStream? logsStream = null;
        FileStream? diagsStream = null;

        try
        {
            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(payload.Description), "description");
            formData.Add(new StringContent(ClientVersionProvider?.Invoke() ?? "1.0.0"), "clientVersion");

            if (!string.IsNullOrEmpty(payload.LogsZipPath) && File.Exists(payload.LogsZipPath))
            {
                logsStream = new FileStream(payload.LogsZipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var logsContent = new StreamContent(logsStream);
                logsContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                formData.Add(logsContent, "logs", "logs.zip");
            }

            if (!string.IsNullOrEmpty(payload.DiagnosticsZipPath) && File.Exists(payload.DiagnosticsZipPath))
            {
                diagsStream = new FileStream(payload.DiagnosticsZipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var diagsContent = new StreamContent(diagsStream);
                diagsContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                formData.Add(diagsContent, "diagnostics", "diagnostics.zip");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/feedback")
            {
                Content = formData,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return FeedbackSubmissionResult.Ok("反馈已成功送达官方团队，感谢您的支持与协助！");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return FeedbackSubmissionResult.Fail("登录凭证已失效，请在社区登录后重试。");
            }

            if ((int)response.StatusCode == 429)
            {
                return FeedbackSubmissionResult.Fail("反馈提交过于频繁，请稍候再试。");
            }

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("message", out var msgElement))
                {
                    var msg = msgElement.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        return FeedbackSubmissionResult.Fail(msg);
                    }
                }
            }
            catch
            {
                // ignore json parse error
            }

            return FeedbackSubmissionResult.Fail($"提交失败 (HTTP {(int)response.StatusCode})，请稍后重试。");
        }
        catch (OperationCanceledException)
        {
            return FeedbackSubmissionResult.Fail("提交已取消");
        }
        catch (Exception exception)
        {
            return FeedbackSubmissionResult.Fail($"网络或服务异常: {exception.Message}", exception);
        }
        finally
        {
            logsStream?.Dispose();
            diagsStream?.Dispose();
        }
    }
}
