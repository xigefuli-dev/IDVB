using System.Net;
using System.Text.Json;
using IDVBuff.Features.Announcements;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AnnouncementFeatureTests
{
    [Fact]
    public void AnnouncementCategories_ReturnsExpectedDisplayNames()
    {
        Assert.Equal("更新公告", AnnouncementCategories.GetDisplayName(AnnouncementCategories.Update));
        Assert.Equal("冷知识", AnnouncementCategories.GetDisplayName(AnnouncementCategories.Tips));
        Assert.Equal("通知", AnnouncementCategories.GetDisplayName(AnnouncementCategories.Notice));
        Assert.Equal("公告", AnnouncementCategories.GetDisplayName("unknown"));
    }

    [Fact]
    public async Task AnnouncementService_FetchesAndCachesAnnouncementsSuccessfully()
    {
        var testAnnouncements = new List<AnnouncementItem>
        {
            new()
            {
                Id = "notice-001",
                Title = "v1.6.1 版本更新说明",
                Category = AnnouncementCategories.Update,
                Tag = "v1.6.1",
                Summary = "全新公告与知识库系统上线",
                Content = "# v1.6.1\n\n- 新增全屏 Markdown 公告与冷知识系统",
                IsPinned = true,
                Priority = 10,
                PublishAt = DateTimeOffset.UtcNow.ToString("O"),
            },
            new()
            {
                Id = "notice-002",
                Title = "冷知识：红教堂侧门速查",
                Category = AnnouncementCategories.Tips,
                Tag = "红教堂",
                Summary = "开局3秒定位侧门",
                Content = "### 技巧\n\n红教堂小地图...",
                IsPinned = false,
                Priority = 5,
                PublishAt = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"),
            }
        };

        var jsonResponse = JsonSerializer.Serialize(new AnnouncementResponse
        {
            Announcements = testAnnouncements,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });

        var testHandler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            });

        var prevClient = AnnouncementService.CustomHttpClient;
        AnnouncementService.CustomHttpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("https://community.idvb.xgflee.com/"),
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "IDVB_Test_Announce_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AnnouncementService(tempDir);
            var list = await service.GetAnnouncementsAsync(forceRefresh: true);

            Assert.Equal(2, list.Count);
            Assert.Equal("notice-001", list[0].Id);
            Assert.Equal(AnnouncementCategories.Update, list[0].Category);

            // 分类筛选
            var tipsList = await service.GetAnnouncementsAsync(category: AnnouncementCategories.Tips, forceRefresh: false);
            Assert.Single(tipsList);
            Assert.Equal("notice-002", tipsList[0].Id);

            // 重要公告识别
            var important = await service.GetImportantUnreadAsync();
            Assert.NotNull(important);
            Assert.Equal("notice-001", important.Id);

            // 标记已读
            await service.MarkAsReadAsync("notice-001");
            Assert.True(list[0].IsRead);

            // 标记不再提示
            await service.DismissPopupAsync("notice-001");
            Assert.True(list[0].IsDismissed);

            // 再次检查重要公告（notice-001 已读且 dismissed，不应再被作为重要未读返回）
            var nextImportant = await service.GetImportantUnreadAsync();
            Assert.Null(nextImportant);
        }
        finally
        {
            AnnouncementService.CustomHttpClient = prevClient;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task AnnouncementService_GracefullyHandlesNetworkErrorsWithoutThrowing()
    {
        var failingHandler = new TestHttpMessageHandler(req =>
            throw new HttpRequestException("Network failure simulating offline"));

        var prevClient = AnnouncementService.CustomHttpClient;
        AnnouncementService.CustomHttpClient = new HttpClient(failingHandler)
        {
            BaseAddress = new Uri("https://community.idvb.xgflee.com/"),
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "IDVB_Test_Announce_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AnnouncementService(tempDir);
            // 即便发生网络异常，也必须安全降级，不向外抛出异常
            var list = await service.GetAnnouncementsAsync(forceRefresh: true);
            Assert.NotNull(list);
        }
        finally
        {
            AnnouncementService.CustomHttpClient = prevClient;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
