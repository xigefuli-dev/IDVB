namespace IDVBuff.Tests;

public sealed class MapListPackageActionPolicyTests
{
    [Fact]
    public void SubscriptionEntryBelongsToTheImportTeachingTip()
    {
        var list = Read("Views", "MapListPage.Part1.cs");
        var catalog = Read("Views", "MapListPage.Catalog.cs");

        Assert.DoesNotContain("CreateActionButton(\"更新订阅\"", list);
        Assert.Contains("choices.Children.Add(updateSubscriptions)", catalog);
        Assert.Contains("ShowMapSubscriptionsDialogAsync(importButton, exportButton)", catalog);
    }

    [Fact]
    public void PublishButtonUsesOneTeachingTipThenOrdinaryDialogs()
    {
        var list = Read("Views", "MapListPage.Part1.cs");
        var catalog = Read("Views", "MapListPage.Catalog.cs");
        var actions = Read("Views", "MapListPage.ExportPublishing.cs");

        Assert.Contains("CreateActionButton(\"发布\"", list);
        Assert.DoesNotContain("CreateActionButton(\"导出\"", list);
        Assert.Contains("CreatePublishTeachingTip(importButton, publishButton)", list);
        Assert.Contains("root.Children.Add(teachingTip)", list);
        Assert.Contains("root.Children.Add(publishTeachingTip)", list);
        Assert.DoesNotContain("root.Children.Add(exportTeachingTip)", list);
        Assert.DoesNotContain("root.Children.Add(websiteTeachingTip)", list);
        Assert.Equal(1, CountOccurrences(actions, "CreatePackageActionTeachingTip("));
        Assert.Contains("CreateTeachingTipChoiceButton(\"导出地图包\")", actions);
        Assert.Contains("CreateTeachingTipChoiceButton(\"发布到官网\")", actions);
        Assert.Contains("await CloseTeachingTipAsync(tip)", actions);
        Assert.Contains("ShowExportIdvmDialogAsync(importButton, publishButton)", actions);
        Assert.Contains("ShowWebsitePublishDialogAsync(importButton, publishButton)", actions);
        Assert.Contains("sender.Closed -= Complete", actions);
        Assert.Contains("publishButton,", actions);
        Assert.Contains("importButton,", catalog);
        Assert.Contains("PublishMapsAsync", actions);
        Assert.Contains("ExportIdvmAsync", actions);
        Assert.DoesNotContain("tip.Title =", actions);
        Assert.DoesNotContain("tip.Subtitle =", actions);
        Assert.DoesNotContain("tip.Content =", actions);
        Assert.DoesNotContain("选择发布目标", actions);
        Assert.Equal(2, CountOccurrences(actions, "new ContentDialog"));
        Assert.Contains("new ComboBox", actions);
        Assert.Contains("new TextBox", actions);
        Assert.Contains("Title = \"选择导出范围\"", actions);
        Assert.Contains("Title = \"发布到 IDVB 官网\"", actions);
    }

    [Fact]
    public void MapListVisibleCopyCallsClassesMapClasses()
    {
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(RepositoryRoot, "Views"),
                    "MapListPage*.cs")
                .Select(File.ReadAllText));

        foreach (var oldCopy in new[]
                 {
                     "当前 Class",
                     "非空 Class",
                     "新建 Class",
                     "重命名 Class",
                     "删除 Class",
                     "创建 Class"
                 })
        {
            Assert.DoesNotContain(oldCopy, source);
        }
        Assert.Contains("当前地图类", source);
        Assert.Contains("新建地图类", source);
    }

    [Fact]
    public void PublicationRejectsMapsObtainedFromSubscriptions()
    {
        var service = Read("Features", "Maps", "MapPublicationService.cs");

        Assert.Contains("map.AcquisitionKind == MapAcquisitionKind.Subscription", service);
        Assert.Contains("订阅获得的地图不能再次发布", service);
    }

    private static string Read(params string[] components) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(components).ToArray()));

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string RepositoryRoot
    {
        get
        {
            for (var current = new DirectoryInfo(Directory.GetCurrentDirectory());
                 current is not null;
                 current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "IDVBuff.slnx")))
                    return current.FullName;
            }
            throw new DirectoryNotFoundException("Unable to locate the repository root.");
        }
    }
}
