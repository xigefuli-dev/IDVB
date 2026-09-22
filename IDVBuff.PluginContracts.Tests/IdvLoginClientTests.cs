using System.Net;
using System.Reflection;
using IDVBuff.Plugins.IdvLogin;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public class IdvLoginClientTests
{
    [Theory]
    [InlineData("{\"success\":true,\"adapter_version\":1}", true)]
    [InlineData("{\"success\":true,\"adapter_version\":2}", true)]
    [InlineData("{\"success\":true,\"adapter_version\":3}", false)]
    [InlineData("{\"success\":true,\"adapter_version\":0}", false)]
    [InlineData("{\"success\":false,\"adapter_version\":1}", false)]
    [InlineData("{}", false)]
    public async Task ReplacementRequiresConfirmedOlderAdapter(string status, bool expected)
    {
        using var client = new IdvLoginClient(new Responses(status));
        Assert.Equal(expected, await client.IsOlderAdapterAsync(default));
    }

    [Theory]
    [InlineData(1, true, "旧版适配层 v1")]
    [InlineData(3, false, "抑制功能暂不可用")]
    [InlineData(2, true, "旧版适配层 v2")]
    [InlineData(3, true, "")]
    public async Task AdapterNoticeDistinguishesOldProcessFromMissingCapability(int version, bool suppress, string expected)
    {
        using var client = new IdvLoginClient(new Responses(System.Text.Json.JsonSerializer.Serialize(new
        {
            success = true, adapter_version = version, account_source = true, suppress_auto_accounts = suppress
        })));
        var notice = await client.GetAdapterNoticeAsync(default);
        if (expected.Length == 0) Assert.Empty(notice);
        else Assert.Contains(expected, notice);
        Assert.DoesNotContain("不兼容", notice);
    }

    [Fact]
    public void EmbeddedInstallationUsesHiddenDirectProcessWithoutShellArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb login ' " + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "python-embed"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "python-embed", "python.exe"), "test fixture");
            File.WriteAllText(Path.Combine(root, "src", "main.pyc"), "test fixture");
            var start = IdvLoginPlugin.ResolveStartInfo(root)!;
            Assert.NotNull(start);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
            Assert.Equal([Path.Combine(AppContext.BaseDirectory, "IdvLoginAdapter", "adapter_bootstrap.py"), root], start.ArgumentList);
            Assert.Null(IdvLoginPlugin.ResolveStartInfo(Path.Combine(root, "missing.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AccountSourceComesFromMetadataAndNeverFromUsername()
    {
        using var client = new IdvLoginClient(new Responses("{\"success\":true,\"status\":\"ok\"}",
            "[{\"uuid\":\"1\",\"name\":\"小米\",\"login_channel\":\"huawei\"},{\"uuid\":\"2\",\"name\":\"华为\"}]"));
        var accounts = await client.GetAccountsAsync(default);
        Assert.Equal("华为", LoginBrand.For(accounts[0].Channel).Name);
        Assert.Equal("未知来源", LoginBrand.For(accounts[1].Channel).Name);
    }

    [Fact]
    public async Task LongLivedRecordingEnablesRecordingBeforeDisablingShortNativeSave()
    {
        var handler = new Responses("{\"success\":true}", "{\"success\":true}");
        using var client = new IdvLoginClient(handler);
        await client.UseLongLivedRecordingAsync(default);
        Assert.Equal(["/_idv-login/scan-record-setting", "/_idv-login/native-save-setting"],
            handler.Requests.Select(uri => uri.AbsolutePath));
    }

    [Fact]
    public async Task AccountLaunchSelectsAndVerifiesBeforeDelegatingStartup()
    {
        var handler = new Responses("{\"success\":true}", "{\"uuid\":\"a&b\"}", "{\"success\":true}");
        using var client = new IdvLoginClient(handler);
        await client.LaunchWithAccountAsync("a&b", default);
        Assert.Equal(["/_idv-login/setDefault", "/_idv-login/defaultChannel", "/_idv-login/start-game"],
            handler.Requests.Select(uri => uri.AbsolutePath));
        Assert.Contains("uuid=a%26b", handler.Requests[0].Query);
        Assert.All(handler.Requests, uri => Assert.Contains("game_id=h55", uri.Query));
    }

    [Theory]
    [InlineData("{\"success\":false}")]
    [InlineData("{}")]
    public async Task RejectedAccountNeverLaunchesGame(string response)
    {
        var handler = new Responses(response);
        using var client = new IdvLoginClient(handler);
        await Assert.ThrowsAsync<LoginLaunchException>(() => client.LaunchWithAccountAsync("a", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChangedOrDeletedAccountNeverLaunchesGame()
    {
        var handler = new Responses("{\"success\":true}", "{\"uuid\":\"other\"}");
        using var client = new IdvLoginClient(handler);
        await Assert.ThrowsAsync<LoginLaunchException>(() => client.LaunchWithAccountAsync("a", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FailedLaunchDoesNotFallBackToFeverOrRepeatSwitch()
    {
        var handler = new Responses("{\"success\":true}", "{\"uuid\":\"a\"}", "{\"success\":false}");
        using var client = new IdvLoginClient(handler);
        await Assert.ThrowsAsync<LoginLaunchException>(() => client.LaunchWithAccountAsync("a", default));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("huawei", "请用手机浏览器扫码（非游戏扫一扫）")]
    [InlineData("myapp", "请用微信扫一扫")]
    [InlineData("bilibili_sdk", "请用哔哩哔哩扫一扫")]
    public void QrInstructionsIdentifyTheScannerForEachChannel(string channel, string message)
    {
        Assert.Equal(message, new LoginImportState(channel, "ready").UserMessage);
        Assert.Equal(message, new LoginImportState(channel, "waiting").UserMessage);
    }

    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue())
            });
        }
    }

    [Fact]
    public async Task EmptyLocalAccountListIsValid_AndOnlyTargetsIdentityV()
    {
        var handler = new Responses("{\"success\":true,\"status\":\"ok\"}", "[]");
        using var client = new IdvLoginClient(handler);
        Assert.Empty(await client.GetAccountsAsync(default));
        Assert.Equal("?game_id=h55", handler.Requests[1].Query);
        Assert.All(handler.Requests, uri => Assert.Equal("localhost", uri.Host));
    }

    [Theory]
    [InlineData("false", LoginSelectionResult.NotCompleted)]
    [InlineData("true", LoginSelectionResult.Submitted)]
    [InlineData("null", LoginSelectionResult.NotCompleted)]
    [InlineData("{}", LoginSelectionResult.Submitted)]
    [InlineData("{\"code\":0}", LoginSelectionResult.Submitted)]
    [InlineData("{\"code\":1424}", LoginSelectionResult.NotCompleted)]
    [InlineData("{\"error\":\"expired\"}", LoginSelectionResult.NotCompleted)]
    [InlineData("{\"user_id\":\"test-user\",\"token\":\"test-token\",\"login_channel\":\"huawei\"}", LoginSelectionResult.WaitingForGame)]
    public async Task SelectionWaitsForFinalResult(string result, LoginSelectionResult expected)
    {
        var handler = new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"done\",\"result\":" + result + "}");
        using var client = new IdvLoginClient(handler);
        Assert.Equal(expected, await client.SelectAccountAsync("a&b", default));
        Assert.Contains("uuid=a%26b", handler.Requests[0].Query);
        Assert.EndsWith("switch-status", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task LegacySelectionDoesNotClaimSuccessfulLogin()
    {
        using var client = new IdvLoginClient(new Responses("{\"current\":\"a\"}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SelectAccountAsync("a", default));
    }

    [Fact]
    public async Task CancellationStopsPendingSelection()
    {
        using var client = new IdvLoginClient(new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}"));
        using var cancellation = new CancellationTokenSource();
        var pending = client.SelectAccountAsync("a", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task PreparedAccountWaitsForGameThenStopsAfterSubmission()
    {
        var handler = new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"done\",\"result\":{\"user_id\":\"test-user\",\"token\":\"test-token\",\"login_channel\":\"huawei\"}}",
            "{\"status\":\"pending\",\"task_id\":\"b\"}", "{\"status\":\"done\",\"result\":{}}");
        using var client = new IdvLoginClient(handler);
        var states = new List<LoginSelectionResult>();
        Assert.Equal(LoginSelectionResult.Submitted, await client.SelectAccountWhenReadyAsync("a", states.Add, default));
        Assert.Equal([LoginSelectionResult.WaitingForGame, LoginSelectionResult.Submitted], states);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("{\"code\":1424}")]
    public async Task FailedSubmissionIsNotRetried(string result)
    {
        var handler = new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"done\",\"result\":" + result + "}");
        using var client = new IdvLoginClient(handler);
        Assert.Equal(LoginSelectionResult.NotCompleted, await client.SelectAccountWhenReadyAsync("a", _ => { }, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownResultIsNotRetriedOrReportedAsAccountExpiry()
    {
        var handler = new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"done\",\"result\":{\"unrecognized\":true}}");
        using var client = new IdvLoginClient(handler);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            client.SelectAccountWhenReadyAsync("a", _ => { }, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void LoginPluginLivesOutsideMatchesButHonorsMasterSwitch()
    {
        var metadata = typeof(IdvLoginPlugin).GetCustomAttribute<PluginAttribute>()!;
        Assert.False(metadata.StrictMatchLifecycle);
        Assert.True(new PluginAttribute("default").StrictMatchLifecycle);
        using var host = new PluginHost(new MessageBus(), new FakeContextFactory());
        var plugin = new IdvLoginPlugin();
        host.SetActivationAllowed(false);
        host.Register(plugin);
        host.Start();
        var lifetime = plugin.Lifetime;
        Assert.True(host.IsActive(plugin.Id));
        host.SetActivationAllowed(true);
        host.SetActivationAllowed(false);
        Assert.False(lifetime.IsCancellationRequested);
        host.SetEnabled(plugin.Id, false);
        Assert.True(lifetime.IsCancellationRequested);
        Assert.False(host.IsActive(plugin.Id));
        host.SetEnabled(plugin.Id, true);
        Assert.False(plugin.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task ImportUsesInlineQrAndOnlySucceedsAfterSavedAccountResult()
    {
        var handler = new Responses(
            "{\"status\":\"ready\",\"qrcode_base64\":\"old\"}",
            "{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"pending\"}",
            "{\"status\":\"ready\",\"qrcode_base64\":\"new\"}",
            "{\"status\":\"done\",\"success\":true}");
        using var client = new IdvLoginClient(handler);
        var states = new List<LoginImportState>();
        await client.ImportAsync(new("bilibili_sdk", "B站"), states.Add, default);
        Assert.Contains("login_method=qr", handler.Requests[1].Query);
        Assert.Equal("new", states[0].QrBase64);
        Assert.False(states[0].Success);
        Assert.True(states[^1].Success);
        Assert.Empty(states[^1].QrBase64);
    }

    [Fact]
    public async Task ImportDoesNotDisplayOldCachedQrOrTreatVerificationAsSaved()
    {
        var handler = new Responses(
            "{\"status\":\"ready\",\"qrcode_base64\":\"old\"}",
            "{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"pending\"}",
            "{\"status\":\"ready\",\"qrcode_base64\":\"old\"}",
            "{\"status\":\"done\",\"success\":false}");
        using var client = new IdvLoginClient(handler);
        var states = new List<LoginImportState>();
        await client.ImportAsync(new("huawei", "华为"), states.Add, default);
        Assert.All(states, state => Assert.Empty(state.QrBase64));
        Assert.Equal("loading", states[0].Status);
        Assert.True(states[^1].Completed);
        Assert.False(states[^1].Success);
    }

    [Fact]
    public async Task NonQrChannelUsesAuthorizationWithoutOpeningAccountManager()
    {
        var handler = new Responses("{\"status\":\"pending\",\"task_id\":\"a\"}",
            "{\"status\":\"done\",\"success\":true}");
        using var client = new IdvLoginClient(handler);
        var states = new List<LoginImportState>();
        await client.ImportAsync(new("xiaomi_app", "小米"), states.Add, default);
        Assert.EndsWith("/import", handler.Requests[0].AbsolutePath);
        Assert.DoesNotContain("login_method", handler.Requests[0].Query);
        Assert.True(states.Single().Success);
    }
}
