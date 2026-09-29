using System.IO.Compression;
using IDVBuff.Features.Feedback;
using Xunit;

namespace IDVBuff.Tests;

public sealed class FeedbackFeatureTests
{
    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("", 0, false)]
    [InlineData("   ", 0, false)]
    [InlineData("0123456789", 10, false)]       // 10 字符英文：要求字符数大于 10，因此 10 字符不合法
    [InlineData("0123456789a", 11, true)]      // 11 字符英文：合法
    [InlineData("一二三四五", 10, false)]       // 5 个汉字 * 2 = 10 字符：不合法
    [InlineData("一二三四五六", 12, true)]      // 6 个汉字 * 2 = 12 字符：合法
    [InlineData("测试反馈a", 9, false)]         // 4 汉字(8) + 1 英文(1) = 9 字符：不合法
    [InlineData("测试反馈ab", 10, false)]       // 4 汉字(8) + 2 英文(2) = 10 字符：不合法
    [InlineData("测试反馈abc", 11, true)]       // 4 汉字(8) + 3 英文(3) = 11 字符：合法
    [InlineData("地图无法对齐，请排查", 20, true)] // 10 个汉字 * 2 = 20 字符：合法
    public void FeedbackTextValidator_CalculatesWeightedLengthAndValidatesCorrectly(
        string? input,
        int expectedLength,
        bool expectedValid)
    {
        int actualLength = FeedbackTextValidator.CalculateWeightedLength(input);
        bool actualValid = FeedbackTextValidator.IsDescriptionValid(input);

        Assert.Equal(expectedLength, actualLength);
        Assert.Equal(expectedValid, actualValid);
    }

    [Fact]
    public async Task FeedbackPackageBuilder_HandlesNonExistentDirectoriesGracefully()
    {
        var nonExistentLogDir = Path.Combine(Path.GetTempPath(), $"idvb_test_logs_{Guid.NewGuid():N}");
        var nonExistentDiagDir = Path.Combine(Path.GetTempPath(), $"idvb_test_diags_{Guid.NewGuid():N}");

        try
        {
            var result = await FeedbackPackageBuilder.BuildPackagesAsync(
                includeLogs: true,
                includeDiagnostics: true,
                customLogDirectory: nonExistentLogDir,
                customDiagnosticsDirectory: nonExistentDiagDir);

            Assert.True(result.Success);
            Assert.NotNull(result.LogsZipPath);
            Assert.NotNull(result.DiagnosticsZipPath);
            Assert.True(File.Exists(result.LogsZipPath));
            Assert.True(File.Exists(result.DiagnosticsZipPath));

            Assert.Equal(0, result.LogsFileCount);
            Assert.Equal(0, result.DiagnosticsFileCount);
        }
        finally
        {
            if (Directory.Exists(nonExistentLogDir)) Directory.Delete(nonExistentLogDir, true);
            if (Directory.Exists(nonExistentDiagDir)) Directory.Delete(nonExistentDiagDir, true);
        }
    }

    [Fact]
    public async Task FeedbackPackageBuilder_PreservesRelativePathsInZip()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), $"idvb_test_pack_{Guid.NewGuid():N}");
        var subDir = Path.Combine(tempSource, "对局 1", "结构配准");
        Directory.CreateDirectory(subDir);

        var file1 = Path.Combine(tempSource, "root_file.log");
        var file2 = Path.Combine(subDir, "alignment.png");
        await File.WriteAllTextAsync(file1, "sample log content");
        await File.WriteAllTextAsync(file2, "image payload bytes");

        try
        {
            var result = await FeedbackPackageBuilder.BuildPackagesAsync(
                includeLogs: true,
                includeDiagnostics: false,
                customLogDirectory: tempSource);

            Assert.True(result.Success);
            Assert.NotNull(result.LogsZipPath);
            Assert.True(File.Exists(result.LogsZipPath));
            Assert.Equal(2, result.LogsFileCount);

            // 检查 ZIP 内部的相对路径是否正确
            {
                using var archive = ZipFile.OpenRead(result.LogsZipPath);
                var entryNames = archive.Entries.Select(e => e.FullName).ToList();

                Assert.Contains("root_file.log", entryNames);
                Assert.Contains("对局 1/结构配准/alignment.png", entryNames);
            }

            // 清理临时目录
            FeedbackPackageBuilder.CleanupTempDirectory(result.OutputDirectory);
            Assert.False(Directory.Exists(result.OutputDirectory));
        }
        finally
        {
            if (Directory.Exists(tempSource))
                Directory.Delete(tempSource, true);
        }
    }

    [Fact]
    public async Task OfficialFeedbackService_RequiresQqWhenNotLoggedIn()
    {
        OfficialFeedbackService.TokenProvider = () => null;
        var service = OfficialFeedbackService.Instance;
        var payload = new FeedbackSubmissionPayload
        {
            Description = "这是一个测试问题反馈描述",
            IncludeLogs = false,
            IncludeDiagnostics = false
        };

        var result = await service.SubmitFeedbackAsync(payload);

        Assert.False(result.Success);
        Assert.Contains("QQ", result.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234", false)]
    [InlineData("012345", false)]
    [InlineData("12345a", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12345", true)]
    [InlineData(" 123456789012 ", true)]
    public void ContactQqValidation(string? value, bool expected)
        => Assert.Equal(expected, FeedbackTextValidator.IsContactQqValid(value));

    [Fact]
    public async Task OfficialFeedbackService_SubmitsGuestQqWithoutAuthorization()
    {
        using var client = new HttpClient(new TestHttpMessageHandler
        {
            Handler = request =>
            {
                Assert.Null(request.Headers.Authorization);
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                var contact = Assert.Single(form.Where(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "contactQq"));
                Assert.Equal("12345678", contact.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            }
        }) { BaseAddress = new Uri("https://community.idvb.test/") };
        OfficialFeedbackService.TokenProvider = () => null;
        OfficialFeedbackService.CustomHttpClient = client;
        try
        {
            var result = await OfficialFeedbackService.Instance.SubmitFeedbackAsync(new FeedbackSubmissionPayload
            {
                Description = "这是未登录用户的问题描述",
                ContactQq = " 12345678 "
            });
            Assert.True(result.Success, result.Message);
        }
        finally
        {
            OfficialFeedbackService.CustomHttpClient = null;
            OfficialFeedbackService.TokenProvider = null;
        }
    }

    [Fact]
    public async Task OfficialFeedbackService_SubmitsSuccessfullyWhenLoggedIn()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("valid-token-123", req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"success\":true,\"feedbackId\":\"fb_123\"}")
                };
            }
        };

        OfficialFeedbackService.TokenProvider = () => "valid-token-123";
        OfficialFeedbackService.ClientVersionProvider = () => "1.4.0-test";
        OfficialFeedbackService.CustomHttpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("https://community.idvb.xgflee.com/")
        };

        var service = OfficialFeedbackService.Instance;
        var payload = new FeedbackSubmissionPayload
        {
            Description = "这是一个测试问题反馈描述",
            IncludeLogs = false,
            IncludeDiagnostics = false
        };

        var result = await service.SubmitFeedbackAsync(payload);

        Assert.True(result.Success);
        Assert.Contains("成功", result.Message);
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.Created);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }
}
